namespace Pointframe.Services;

public interface ISystemAudioNoticeService
{
    void ReportUnavailable(string reason, string phase);
}

// Tracks every failure but tells the user once per app run, through a tray balloon, so a recording is never interrupted.
internal sealed class SystemAudioNoticeService : ISystemAudioNoticeService
{
    internal const string Title = "System audio unavailable";
    internal const string Message = "Pointframe could not record system audio, so this recording has no system audio. The video was kept.";

    private readonly ITrayIconManager _trayIcon;
    private readonly ITelemetryService _telemetry;
    private readonly object _gate = new();
    private bool _notified;

    public SystemAudioNoticeService(ITrayIconManager trayIcon, ITelemetryService telemetry)
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

    public void ReportUnavailable(string reason, string phase)
    {
        _telemetry.TrackEvent(TelemetryEvents.SystemAudioUnavailable, new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.Reason] = reason,
            [TelemetryPropertyKeys.Phase] = phase,
        });

        lock (_gate)
        {
            if (_notified)
            {
                return;
            }

            _notified = true;
        }

        RunOnUiThread(() => _trayIcon.ShowTranscriptBalloon(Title, Message, isError: true));
    }
}
