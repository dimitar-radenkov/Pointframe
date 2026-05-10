namespace Pointframe.Services;

public interface IScreenRecordingService : IDisposable
{
    bool IsRecording { get; }
    bool IsPaused { get; }
    bool IsRecordingMicrophoneEnabled { get; }
    bool CanToggleMicrophone { get; }
    bool IsMicrophoneMuted { get; }
    void Start(
        int x,
        int y,
        int width,
        int height,
        string outputPath);
    void Stop();
    void Pause();
    void Resume();
    bool TrySetMicrophoneMuted(bool isMuted);
}
