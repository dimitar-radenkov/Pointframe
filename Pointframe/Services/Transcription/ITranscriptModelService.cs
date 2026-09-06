namespace Pointframe.Services;

public interface ITranscriptModelService
{
    bool IsModelInstalled { get; }

    string? ResolveModelPath();

    long ExpectedDownloadBytes { get; }

    Task<bool> DownloadModel(IProgress<double>? progress, CancellationToken cancellationToken = default);
}
