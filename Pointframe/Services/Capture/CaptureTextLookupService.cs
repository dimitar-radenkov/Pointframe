using Microsoft.Extensions.DependencyInjection;
using Pointframe.Data.Abstractions;
using Pointframe.Data.Entities;

namespace Pointframe.Services;

internal sealed class CaptureTextLookupService : ICaptureTextLookupService
{
    private const int DefaultMaxCacheEntries = 2000;
    private static readonly TimeSpan LastAccessWriteInterval = TimeSpan.FromMinutes(10);

    private readonly IImageFileService _imageFiles;
    private readonly IOcrService _ocr;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly int _maxCacheEntries;

    public CaptureTextLookupService(
        IImageFileService imageFiles,
        IOcrService ocr,
        IServiceScopeFactory scopeFactory,
        int maxCacheEntries = DefaultMaxCacheEntries)
    {
        _imageFiles = imageFiles;
        _ocr = ocr;
        _scopeFactory = scopeFactory;
        _maxCacheEntries = Math.Max(1, maxCacheEntries);
    }

    public async Task<string?> GetText(CaptureItem item, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<IPointframeDataUnitOfWork>();
        CaptureTextCacheEntry? existing = null;

        try
        {
            existing = await data.CaptureTextCache.GetByFilePath(item.FilePath, cancellationToken);
            if (existing is not null && existing.CapturedAt == item.CapturedAtUtc)
            {
                var now = DateTime.UtcNow;
                if (now - existing.LastAccessedAt >= LastAccessWriteInterval)
                {
                    existing.LastAccessedAt = now;
                    await data.CaptureTextCache.Update(existing, cancellationToken);
                    await data.SaveChanges(cancellationToken);
                }

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
                await data.CaptureTextCache.Add(new CaptureTextCacheEntry
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
                await data.CaptureTextCache.Update(existing, cancellationToken);
            }

            await data.CaptureTextCache.TrimToLimit(_maxCacheEntries, cancellationToken);
            await data.SaveChanges(cancellationToken);
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
