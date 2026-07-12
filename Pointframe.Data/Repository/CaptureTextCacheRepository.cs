using Microsoft.EntityFrameworkCore;
using Pointframe.Data.Abstractions;
using Pointframe.Data.Context;
using Pointframe.Data.Entities;

namespace Pointframe.Data.Repository;

public sealed class CaptureTextCacheRepository : Repository<CaptureTextCacheEntry>, ICaptureTextCacheRepository
{
    public CaptureTextCacheRepository(PointframeDataContext context)
        : base(context)
    {
    }

    public Task<CaptureTextCacheEntry?> GetByFilePath(string filePath, CancellationToken cancellationToken = default)
    {
        return DbSet.FirstOrDefaultAsync(entry => entry.FilePath == filePath, cancellationToken);
    }

    public async Task TrimToLimit(int maxEntries, CancellationToken cancellationToken = default)
    {
        var boundedMax = Math.Max(1, maxEntries);
        var toRemove = await DbSet
            .OrderByDescending(entry => entry.LastAccessedAt)
            .Skip(boundedMax)
            .ToListAsync(cancellationToken);

        if (toRemove.Count == 0)
        {
            return;
        }

        DbSet.RemoveRange(toRemove);
    }
}
