namespace Pointframe.Services;

public interface IMicrophoneWarningService
{
    void ReportIfUnavailable(bool recordingMicrophoneEnabled);
}
