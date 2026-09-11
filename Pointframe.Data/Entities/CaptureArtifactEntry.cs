using Pointframe.Data.Abstractions;

namespace Pointframe.Data.Entities;

public sealed class CaptureArtifactEntry : IEntity
{
    public string ArtifactId { get; set; } = string.Empty;

    public string Kind { get; set; } = "screenshot";

    public string MimeType { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;

    public long ByteLength { get; set; }

    public int PixelWidth { get; set; }

    public int PixelHeight { get; set; }

    public DateTime CapturedAtUtc { get; set; }

    public CaptureTimestampSource TimestampSource { get; set; }

    public CaptureArtifactSource Source { get; set; }

    public string? ProvenanceJson { get; set; }

    public CaptureArtifactAvailability Availability { get; set; }

    public CaptureOcrStatus OcrStatus { get; set; }

    public string? OcrText { get; set; }

    public string? SearchTextNormalized { get; set; }

    public string? IndexedSha256 { get; set; }

    public string? OcrEngineVersion { get; set; }

    public DateTime? IndexedAtUtc { get; set; }

    public int AttemptCount { get; set; }

    public DateTime? NextAttemptUtc { get; set; }

    public string? LeaseOwner { get; set; }

    public DateTime? LeaseExpiresUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public ICollection<CaptureLocationEntry> Locations { get; set; } = new List<CaptureLocationEntry>();
}

public enum CaptureTimestampSource
{
    Capture,
    Sidecar,
    FileLastWrite,
}

public enum CaptureArtifactSource
{
    WpfSave,
    WpfSaveAs,
    WpfAutoSave,
    WpfBeautifier,
    DirectMonitor,
    DirectWindow,
    Import,
}

public enum CaptureArtifactAvailability
{
    Available,
    Missing,
    Superseded,
    Unreadable,
}

public enum CaptureOcrStatus
{
    Pending,
    Processing,
    Ready,
    NoText,
    Unavailable,
    Failed,
    Unsupported,
}
