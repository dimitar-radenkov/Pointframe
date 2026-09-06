namespace Pointframe.Services;

public sealed record TranscriptionCompletion(
    TranscriptionResult Result,
    string VideoPath,
    TimeSpan Duration);

public interface ITranscriptionQueue
{
    int PendingCount { get; }

    bool IsBusy { get; }

    event Action? ActivityChanged;

    event Action<TranscriptionCompletion>? Completed;

    void Enqueue(string videoPath);

    Task<bool> WaitForIdle(TimeSpan timeout);

    void CancelAll();
}
