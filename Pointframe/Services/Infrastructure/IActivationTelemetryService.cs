namespace Pointframe.Services;

public interface IActivationTelemetryService
{
    void TrackCaptureCompleted(string captureAction);

    void TrackRecordingCompleted(string elapsedText);
}
