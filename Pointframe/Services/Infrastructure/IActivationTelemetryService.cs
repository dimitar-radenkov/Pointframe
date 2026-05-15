namespace Pointframe.Services;

public interface IActivationTelemetryService
{
    void TrackCaptureCompleted();

    void TrackRecordingCompleted(string elapsedText);
}
