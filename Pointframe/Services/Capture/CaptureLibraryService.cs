using Pointframe.Engine;

namespace Pointframe.Services;

internal sealed class CaptureLibraryService : ICaptureLibraryService
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp" };

    private readonly IUserSettingsService _settings;
    private readonly ICaptureTextLookupService _textIndex;
    private readonly ICaptureCatalogService? _catalog;

    public CaptureLibraryService(IUserSettingsService settings, ICaptureTextLookupService textIndex)
        : this(settings, textIndex, null)
    {
    }

    public CaptureLibraryService(
        IUserSettingsService settings,
        ICaptureTextLookupService textIndex,
        ICaptureCatalogService? catalog)
    {
        _settings = settings;
        _textIndex = textIndex;
        _catalog = catalog;
    }

    public IReadOnlyList<CaptureItem> GetCaptures()
    {
        return CollectCaptures(null, null, null, useFileNameSearchPattern: false);
    }

    public IReadOnlyList<CaptureItem> Search(string? query, DateTime? fromUtc, DateTime? toUtc)
    {
        return CollectCaptures(fromUtc, toUtc, query, useFileNameSearchPattern: true);
    }

    public async Task<IReadOnlyList<CaptureItem>> SearchAsync(
        string? query,
        DateTime? fromUtc,
        DateTime? toUtc,
        IProgress<CaptureSearchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_catalog is not null)
        {
            return await SearchCatalogAsync(query, fromUtc, toUtc, cancellationToken).ConfigureAwait(false);
        }

        var normalizedQuery = query?.Trim();
        var candidates = CollectCaptures(
            fromUtc,
            toUtc,
            normalizedQuery is { Length: < 3 } ? normalizedQuery : null,
            useFileNameSearchPattern: true,
            sortByCapturedAtDescending: normalizedQuery is null or { Length: < 3 });
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return candidates;
        }

        // Keep typing responsive: do not OCR-scan the whole library for very short
        // terms while the user is still composing the search phrase.
        if (normalizedQuery.Length < 3)
        {
            return candidates;
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

        matches.Sort((left, right) => right.CapturedAtUtc.CompareTo(left.CapturedAtUtc));
        return matches;
    }

    private async Task<IReadOnlyList<CaptureItem>> SearchCatalogAsync(
        string? query,
        DateTime? fromUtc,
        DateTime? toUtc,
        CancellationToken cancellationToken)
    {
        var items = new List<CaptureItem>();
        string? cursor = null;
        do
        {
            var page = await _catalog!.SearchAsync(
                new CaptureCatalogSearchRequest(
                    query,
                    fromUtc is null ? null : new DateTimeOffset(DateTime.SpecifyKind(fromUtc.Value, DateTimeKind.Utc)),
                    toUtc is null ? null : new DateTimeOffset(DateTime.SpecifyKind(toUtc.Value, DateTimeKind.Utc)),
                    100,
                    cursor),
                cancellationToken).ConfigureAwait(false);
            foreach (var result in page.Items)
            {
                var artifact = await _catalog.GetAsync(result.ArtifactId, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(artifact?.LocalPath) && File.Exists(artifact.LocalPath))
                {
                    items.Add(new CaptureItem(artifact.LocalPath, artifact.FileName, artifact.CapturedAtUtc.UtcDateTime));
                }
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return items;
    }

    private static bool InDateRange(CaptureItem item, DateTime? fromUtc, DateTime? toUtc)
        => (fromUtc is null || item.CapturedAtUtc >= fromUtc.Value)
            && (toUtc is null || item.CapturedAtUtc <= toUtc.Value);

    private IReadOnlyList<CaptureItem> CollectCaptures(
        DateTime? fromUtc,
        DateTime? toUtc,
        string? fileNameContains,
        bool useFileNameSearchPattern,
        bool sortByCapturedAtDescending = true)
    {
        var folder = _settings.Current.ScreenshotSavePath;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return Array.Empty<CaptureItem>();
        }

        var hasFileNameFilter = !string.IsNullOrWhiteSpace(fileNameContains);

        try
        {
            var captures = new List<CaptureItem>();
            var filePaths = useFileNameSearchPattern && hasFileNameFilter && CanUseFileSearchPattern(fileNameContains!)
                ? Directory.EnumerateFiles(folder, $"*{fileNameContains}*")
                : Directory.EnumerateFiles(folder);

            foreach (var path in filePaths)
            {
                if (!SupportedExtensions.Contains(Path.GetExtension(path)))
                {
                    continue;
                }

                try
                {
                    var capture = new CaptureItem(
                        path,
                        Path.GetFileName(path),
                        File.GetLastWriteTimeUtc(path));

                    if (!InDateRange(capture, fromUtc, toUtc))
                    {
                        continue;
                    }

                    if (hasFileNameFilter && !capture.FileName.Contains(fileNameContains!, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    captures.Add(capture);
                }
                catch (IOException)
                {
                    // Keep capture loading best-effort if a file disappears or cannot be read.
                }
                catch (UnauthorizedAccessException)
                {
                    // Keep capture loading best-effort if file access is denied.
                }
                catch (System.Security.SecurityException)
                {
                    // Keep capture loading best-effort if file metadata access is blocked.
                }
            }

            if (sortByCapturedAtDescending)
            {
                captures.Sort((left, right) => right.CapturedAtUtc.CompareTo(left.CapturedAtUtc));
            }

            return captures;
        }
        catch (IOException)
        {
            return Array.Empty<CaptureItem>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<CaptureItem>();
        }
        catch (System.Security.SecurityException)
        {
            return Array.Empty<CaptureItem>();
        }
    }

    private static bool CanUseFileSearchPattern(string fileNameContains)
    {
        if (fileNameContains.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        return fileNameContains.IndexOfAny(['*', '?']) < 0;
    }
}
