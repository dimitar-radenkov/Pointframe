using Pointframe.Data.Entities;

namespace Pointframe.Data.Abstractions;

public interface ICaptureCatalogRepository
{
    Task<CaptureArtifactEntry?> GetArtifactById(
        string artifactId,
        CancellationToken cancellationToken = default);

    Task<CaptureLocationEntry?> GetLocationByNormalizedPath(
        string normalizedPath,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CaptureArtifactEntry>> GetPendingOcrArtifacts(
        DateTime nowUtc,
        int limit,
        CancellationToken cancellationToken = default);

    Task AddArtifact(CaptureArtifactEntry artifact, CancellationToken cancellationToken = default);

    Task AddLocation(CaptureLocationEntry location, CancellationToken cancellationToken = default);

    Task<CaptureCatalogRegistration> RegisterObservedArtifact(
        CaptureArtifactEntry artifact,
        CaptureLocationEntry location,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CaptureLocationEntry>> GetLocationsUnderRoot(
        string normalizedRoot,
        CancellationToken cancellationToken = default);

    Task MarkLocationsMissing(
        IReadOnlyCollection<string> normalizedPaths,
        DateTime observedAtUtc,
        CancellationToken cancellationToken = default);
}

public sealed record CaptureCatalogRegistration(string ArtifactId, bool AlreadyRegistered, bool IdentityConflict);
