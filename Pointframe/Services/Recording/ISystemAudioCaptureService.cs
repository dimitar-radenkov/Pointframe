namespace Pointframe.Services;

public readonly record struct SystemAudioStopResult(string? WavPath, string? FailureReason)
{
    public bool HasAudio => WavPath is not null;
}

public interface ISystemAudioCaptureService : IDisposable
{
    bool IsCapturing { get; }
    event Action<string>? CaptureFailed;
    bool TryStart(string wavPath, out string? failureReason);
    void BeginTimeline();
    void ExcludeSpan(TimeSpan start, TimeSpan duration);
    void Pause();
    void Resume();
    SystemAudioStopResult Stop();
}
