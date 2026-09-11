using Pointframe.Data.Abstractions;

namespace Pointframe.Data.Entities;

public sealed class CaptureLocationEntry : IEntity
{
    public string NormalizedPath { get; set; } = string.Empty;

    public string OriginalPath { get; set; } = string.Empty;

    public string? CurrentArtifactId { get; set; }

    public DateTime LastWriteAtUtc { get; set; }

    public long ByteLength { get; set; }

    public DateTime LastObservedAtUtc { get; set; }

    public CaptureArtifactEntry? CurrentArtifact { get; set; }
}
