namespace Pointframe.Services;

public interface ITranscriptionService
{
    Task<TranscriptionResult> TranscribeVideoAsync(string videoPath, CancellationToken ct);
}
