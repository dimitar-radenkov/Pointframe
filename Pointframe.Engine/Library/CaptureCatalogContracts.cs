namespace Pointframe.Engine;

public interface ICaptureCatalogService
{
    Task<CaptureRegistrationResult> RegisterAsync(
        CaptureRegistrationRequest request,
        CancellationToken cancellationToken = default);

    Task<CaptureCatalogSearchResult> SearchAsync(
        CaptureCatalogSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<CaptureCatalogArtifact?> GetAsync(
        string artifactId,
        CancellationToken cancellationToken = default);
}

public interface ICaptureImportService
{
    Task RequestReconciliationAsync(CancellationToken cancellationToken = default);
}

public interface ICaptureRegistrationService
{
    Task RegisterOrQueueAsync(CaptureRegistrationRequest request, CancellationToken cancellationToken = default);

    Task ReplayPendingAsync(CancellationToken cancellationToken = default);
}

public interface ICaptureIndexWorker
{
    Task RunOnceAsync(CancellationToken cancellationToken = default);

    Task RunUntilCancelledAsync(CancellationToken cancellationToken = default);
}

public interface ICaptureArtifactReader
{
    Task<CaptureArtifactSnapshot> ReadAsync(
        string artifactId,
        CancellationToken cancellationToken = default);
}

public interface ICaptureLibrarySources
{
    IReadOnlyList<string> GetImportRoots();
}

public sealed record CaptureRegistrationRequest(
    string Path,
    string? ArtifactId,
    string Source,
    DateTimeOffset CapturedAtUtc,
    string TimestampSource,
    string? ProvenanceJson = null,
    string? ExpectedSha256 = null);

public sealed record CaptureRegistrationResult(string ArtifactId, bool AlreadyRegistered);

public sealed record CaptureCatalogSearchRequest(
    string? Query,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    int Limit,
    string? Cursor = null);

public sealed record CaptureCatalogSearchResult(
    IReadOnlyList<CaptureCatalogSearchItem> Items,
    string? NextCursor,
    CaptureCatalogIndexState IndexState);

public sealed record CaptureCatalogSearchItem(
    string ArtifactId,
    string FileName,
    DateTimeOffset CapturedAtUtc,
    string TimestampSource,
    string Source,
    string MimeType,
    int PixelWidth,
    int PixelHeight,
    string Sha256,
    string OcrStatus,
    IReadOnlyList<string> MatchedFields,
    string? Snippet);

public sealed record CaptureCatalogIndexState(
    bool DiscoveryInProgress,
    int PendingCount,
    int FailedCount,
    int UnavailableCount,
    DateTimeOffset? LastReconciledAtUtc);

public sealed record CaptureCatalogArtifact(
    string ArtifactId,
    string FileName,
    string Sha256,
    string MimeType,
    long ByteLength,
    int PixelWidth,
    int PixelHeight,
    DateTimeOffset CapturedAtUtc,
    string Availability,
    string OcrStatus,
    string? OcrText,
    string? ProvenanceJson,
    string? LocalPath);

public sealed record CaptureArtifactSnapshot(
    CaptureCatalogArtifact Artifact,
    Stream Content);
