namespace Pointframe.Services;

internal interface ITrayIconManager : IDisposable
{
    void Initialize();

    void AddDebugMenuItems();

    void HandleUpdateAvailable(Models.UpdateCheckResult result);

    void HandleRecordingCompleted(string outputPath, string elapsedText);

    void HandleCaptureCompleted(string outputPath);
}
