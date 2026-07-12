namespace Pointframe.Services;

public interface ICaptureLibraryService
{
    IReadOnlyList<CaptureItem> GetCaptures();

    IReadOnlyList<CaptureItem> Search(string? query, DateTime? fromUtc, DateTime? toUtc);

    Task<IReadOnlyList<CaptureItem>> SearchAsync(
        string? query,
        DateTime? fromUtc,
        DateTime? toUtc,
        IProgress<CaptureSearchProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
