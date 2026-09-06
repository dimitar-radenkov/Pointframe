namespace Pointframe.Services;

internal sealed class NullTranscriptModelService : ITranscriptModelService
{
    public static NullTranscriptModelService Instance { get; } = new();

    private NullTranscriptModelService()
    {
    }

    public bool IsModelInstalled => false;

    public string? ResolveModelPath() => null;

    public long ExpectedDownloadBytes => 0;

    public Task<bool> DownloadModel(IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }
}
