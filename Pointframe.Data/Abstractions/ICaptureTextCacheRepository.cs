using Pointframe.Data.Entities;

namespace Pointframe.Data.Abstractions;

public interface ICaptureTextCacheRepository : IRepository<CaptureTextCacheEntry>
{
    Task<CaptureTextCacheEntry?> GetByFilePath(string filePath, CancellationToken cancellationToken = default);

    Task TrimToLimit(int maxEntries, CancellationToken cancellationToken = default);
}
