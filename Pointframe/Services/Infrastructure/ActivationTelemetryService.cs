namespace Pointframe.Services;

public sealed class ActivationTelemetryService : IActivationTelemetryService
{
    private readonly ITelemetryService _telemetry;
    private readonly IUserSettingsService _userSettings;

    public ActivationTelemetryService(
        ITelemetryService telemetry,
        IUserSettingsService userSettings)
    {
        _telemetry = telemetry;
        _userSettings = userSettings;
    }

    public void TrackCaptureCompleted(string captureAction)
    {
        _telemetry.TrackEvent(TelemetryEvents.CaptureCompleted, new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.Action] = captureAction,
        });

        var shouldTrackFirstCapture = false;
        int? timeFromInstallMinutes = null;

        _userSettings.Update(settings =>
        {
            if (settings.FirstCaptureCompletedTracked)
            {
                return;
            }

            settings.FirstCaptureCompletedTracked = true;
            shouldTrackFirstCapture = true;
            timeFromInstallMinutes = GetTimeFromInstallMinutes(settings.InstallCreatedUtc);
        });

        if (!shouldTrackFirstCapture)
        {
            return;
        }

        var props = new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.CaptureType] = "screenshot",
            [TelemetryPropertyKeys.FirstAction] = captureAction,
        };

        if (timeFromInstallMinutes is not null)
        {
            props[TelemetryPropertyKeys.TimeFromInstallMinutes] = timeFromInstallMinutes.Value.ToString();
        }

        _telemetry.TrackEvent(TelemetryEvents.FirstCaptureCompleted, props);
    }

    public void TrackRecordingCompleted(string elapsedText)
    {
        var durationSeconds = TryGetDurationSeconds(elapsedText);
        Dictionary<string, string>? recordingProps = null;

        if (durationSeconds is not null)
        {
            recordingProps = new Dictionary<string, string>
            {
                [TelemetryPropertyKeys.DurationSeconds] = durationSeconds.Value.ToString(),
            };
        }

        _telemetry.TrackEvent(TelemetryEvents.RecordingCompleted, recordingProps);

        var shouldTrackFirstRecording = false;
        int? timeFromInstallMinutes = null;
        var withAudio = false;

        _userSettings.Update(settings =>
        {
            if (settings.FirstRecordingCompletedTracked)
            {
                return;
            }

            settings.FirstRecordingCompletedTracked = true;
            shouldTrackFirstRecording = true;
            timeFromInstallMinutes = GetTimeFromInstallMinutes(settings.InstallCreatedUtc);
            withAudio = settings.RecordMicrophone;
        });

        if (!shouldTrackFirstRecording)
        {
            return;
        }

        var firstRecordingProps = new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.WithAudio] = withAudio ? "true" : "false",
        };

        if (durationSeconds is not null)
        {
            firstRecordingProps[TelemetryPropertyKeys.DurationSeconds] = durationSeconds.Value.ToString();
        }

        if (timeFromInstallMinutes is not null)
        {
            firstRecordingProps[TelemetryPropertyKeys.TimeFromInstallMinutes] = timeFromInstallMinutes.Value.ToString();
        }

        _telemetry.TrackEvent(TelemetryEvents.FirstRecordingCompleted, firstRecordingProps);
    }

    private static int? TryGetDurationSeconds(string elapsedText)
    {
        if (TimeSpan.TryParseExact(elapsedText, @"mm\:ss", null, out var duration))
        {
            return (int)duration.TotalSeconds;
        }

        return null;
    }

    private static int? GetTimeFromInstallMinutes(DateTime? installCreatedUtc)
    {
        if (installCreatedUtc is null)
        {
            return null;
        }

        var utcInstallTime = installCreatedUtc.Value.Kind switch
        {
            DateTimeKind.Utc => installCreatedUtc.Value,
            DateTimeKind.Local => installCreatedUtc.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(installCreatedUtc.Value, DateTimeKind.Utc),
        };

        var elapsed = DateTime.UtcNow - utcInstallTime;
        if (elapsed < TimeSpan.Zero)
        {
            return null;
        }

        return (int)elapsed.TotalMinutes;
    }
}
