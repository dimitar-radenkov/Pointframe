namespace SnippingTool.Services;

public interface IScreenRecordingService : IDisposable
{
    bool IsRecording { get; }
    void Start(int x, int y, int width, int height, string outputPath, int fps = 20);
    void Stop();
}
