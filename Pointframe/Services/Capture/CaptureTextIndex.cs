using Pointframe.Data.Abstractions;
using Pointframe.Data.Entities;

namespace Pointframe.Services;

internal sealed class CaptureTextIndex : ICaptureTextIndex
{
    private const int DefaultMaxCacheEntries = 2000;

    private readonly IImageFileService _imageFiles;
    private readonly IOcrService _ocr;
    private readonly IPointframeDataUnitOfWork _data;
    private readonly int _maxCacheEntries;

    public CaptureTextIndex(
        IImageFileService imageFiles,
        IOcrService ocr,
        IPointframeDataUnitOfWork data,
        int maxCacheEntries = DefaultMaxCacheEntries)
    {
        _imageFiles = imageFiles;
        _ocr = ocr;
        _data = data;
        _maxCacheEntries = Math.Max(1, maxCacheEntries);
    }

    public async Task<string?> GetText(CaptureItem item, CancellationToken cancellationToken = default)
    {
        CaptureTextCacheEntry? existing = null;

        try
        {
            existing = await _data.CaptureTextCache.GetByFilePath(item.FilePath, cancellationToken);
            if (existing is not null && existing.CapturedAt == item.CapturedAtUtc)
            {
                existing.LastAccessedAt = DateTime.UtcNow;
                await _data.CaptureTextCache.Update(existing, cancellationToken);
                await _data.SaveChanges(cancellationToken);
                return existing.Text;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // If the cache store is unavailable, continue with OCR and return best-effort results.
        }

        var bitmap = _imageFiles.LoadForAnnotation(item.FilePath);
        var text = await _ocr.Recognize(bitmap, cancellationToken);

        try
        {
            var now = DateTime.UtcNow;
            if (existing is null)
            {
                await _data.CaptureTextCache.Add(new CaptureTextCacheEntry
                {
                    FilePath = item.FilePath,
                    CapturedAt = item.CapturedAtUtc,
                    Text = text,
                    LastAccessedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                }, cancellationToken);
            }
            else
            {
                existing.CapturedAt = item.CapturedAtUtc;
                existing.Text = text;
                existing.LastAccessedAt = now;
                existing.UpdatedAt = now;
                await _data.CaptureTextCache.Update(existing, cancellationToken);
            }

            await _data.CaptureTextCache.TrimToLimit(_maxCacheEntries, cancellationToken);
            await _data.SaveChanges(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Persisted caching failures should not block OCR results.
        }

        return text;
    }
}
