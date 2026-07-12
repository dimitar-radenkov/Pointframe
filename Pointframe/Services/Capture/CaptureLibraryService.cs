namespace Pointframe.Services;

internal sealed class CaptureLibraryService : ICaptureLibraryService
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp" };

    private readonly IUserSettingsService _settings;
    private readonly ICaptureTextLookupService _textIndex;

    public CaptureLibraryService(IUserSettingsService settings, ICaptureTextLookupService textIndex)
    {
        _settings = settings;
        _textIndex = textIndex;
    }

    public IReadOnlyList<CaptureItem> GetCaptures()
    {
        var folder = _settings.Current.ScreenshotSavePath;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return Array.Empty<CaptureItem>();
        }

        return Directory.EnumerateFiles(folder)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .Select(path => new CaptureItem(
                path,
                Path.GetFileName(path),
                File.GetLastWriteTimeUtc(path)))
            .OrderByDescending(item => item.CapturedAtUtc)
            .ToList();
    }

    public IReadOnlyList<CaptureItem> Search(string? query, DateTime? fromUtc, DateTime? toUtc)
    {
        var hasQuery = !string.IsNullOrWhiteSpace(query);

        return GetCaptures()
            .Where(item =>
                (!hasQuery || item.FileName.Contains(query!, StringComparison.OrdinalIgnoreCase))
                && InDateRange(item, fromUtc, toUtc))
            .ToList();
    }

    public async Task<IReadOnlyList<CaptureItem>> SearchAsync(
        string? query,
        DateTime? fromUtc,
        DateTime? toUtc,
        IProgress<CaptureSearchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var candidates = GetCaptures()
            .Where(item => InDateRange(item, fromUtc, toUtc))
            .ToList();

        var normalizedQuery = query?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return candidates;
        }

        // Keep typing responsive: do not OCR-scan the whole library for very short
        // terms while the user is still composing the search phrase.
        if (normalizedQuery.Length < 3)
        {
            return candidates
                .Where(item => item.FileName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var matches = new List<CaptureItem>();
        var scanned = 0;

        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A file-name hit short-circuits OCR — otherwise every keystroke would
            // recognize text in every capture that is only excluded by name.
            if (item.FileName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(item);
            }
            else
            {
                try
                {
                    var text = await _textIndex.GetText(item, cancellationToken);
                    if (text is not null && text.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(item);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // A single unreadable image or OCR failure should not fail the whole search.
                }
            }

            scanned++;
            progress?.Report(new CaptureSearchProgress(scanned, candidates.Count));
        }

        return matches;
    }

    private static bool InDateRange(CaptureItem item, DateTime? fromUtc, DateTime? toUtc)
        => (fromUtc is null || item.CapturedAtUtc >= fromUtc.Value)
            && (toUtc is null || item.CapturedAtUtc <= toUtc.Value);
}
