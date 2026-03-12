namespace SnippingTool.Services;

public interface IScreenRecordingService : IDisposable
{
    bool IsRecording { get; }
    bool IsPaused { get; }
    void Start(
        int x,
        int y,
        int width,
        int height,
        string outputPath);
    void Stop();
    void Pause();
    void Resume();
}
