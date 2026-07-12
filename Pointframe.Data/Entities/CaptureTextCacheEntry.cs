using Pointframe.Data.Abstractions;

namespace Pointframe.Data.Entities;

public sealed class CaptureTextCacheEntry : IEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string FilePath { get; set; } = string.Empty;

    public DateTime CapturedAt { get; set; }

    public string? Text { get; set; }

    public DateTime LastAccessedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
