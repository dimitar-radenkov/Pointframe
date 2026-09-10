using Microsoft.EntityFrameworkCore;
using Pointframe.Data.Abstractions;
using Pointframe.Data.Context;
using Pointframe.Data.Entities;

namespace Pointframe.Data.Repository;

public sealed class CaptureCatalogRepository : ICaptureCatalogRepository
{
    private readonly PointframeDataContext _context;

    public CaptureCatalogRepository(PointframeDataContext context)
    {
        _context = context;
    }

    public Task<CaptureArtifactEntry?> GetArtifactById(
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        return _context.CaptureArtifacts
            .Include(artifact => artifact.Locations)
            .SingleOrDefaultAsync(artifact => artifact.ArtifactId == artifactId, cancellationToken);
    }

    public Task<CaptureLocationEntry?> GetLocationByNormalizedPath(
        string normalizedPath,
        CancellationToken cancellationToken = default)
    {
        return _context.CaptureLocations
            .Include(location => location.CurrentArtifact)
            .SingleOrDefaultAsync(location => location.NormalizedPath == normalizedPath, cancellationToken);
    }

    public async Task<IReadOnlyList<CaptureArtifactEntry>> GetPendingOcrArtifacts(
        DateTime nowUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, 100);
        return await _context.CaptureArtifacts
            .Where(artifact =>
                artifact.Availability == CaptureArtifactAvailability.Available &&
                artifact.OcrStatus == CaptureOcrStatus.Pending &&
                (artifact.NextAttemptUtc == null || artifact.NextAttemptUtc <= nowUtc))
            .OrderBy(artifact => artifact.NextAttemptUtc)
            .ThenBy(artifact => artifact.CapturedAtUtc)
            .ThenBy(artifact => artifact.ArtifactId)
            .Take(boundedLimit)
            .ToListAsync(cancellationToken);
    }

    public Task AddArtifact(CaptureArtifactEntry artifact, CancellationToken cancellationToken = default)
    {
        return _context.CaptureArtifacts.AddAsync(artifact, cancellationToken).AsTask();
    }

    public Task AddLocation(CaptureLocationEntry location, CancellationToken cancellationToken = default)
    {
        return _context.CaptureLocations.AddAsync(location, cancellationToken).AsTask();
    }

    public async Task<CaptureCatalogRegistration> RegisterObservedArtifact(
        CaptureArtifactEntry artifact,
        CaptureLocationEntry location,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        var existingLocation = await _context.CaptureLocations
            .Include(candidate => candidate.CurrentArtifact)
            .SingleOrDefaultAsync(candidate => candidate.NormalizedPath == location.NormalizedPath, cancellationToken);

        if (existingLocation?.CurrentArtifact is { } currentArtifact &&
            string.Equals(currentArtifact.Sha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            currentArtifact.Availability = CaptureArtifactAvailability.Available;
            currentArtifact.UpdatedAtUtc = artifact.UpdatedAtUtc;
            existingLocation.OriginalPath = location.OriginalPath;
            existingLocation.LastWriteAtUtc = location.LastWriteAtUtc;
            existingLocation.ByteLength = location.ByteLength;
            existingLocation.LastObservedAtUtc = location.LastObservedAtUtc;
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CaptureCatalogRegistration(currentArtifact.ArtifactId, true, false);
        }

        var existingArtifact = await _context.CaptureArtifacts
            .Include(candidate => candidate.Locations)
            .SingleOrDefaultAsync(candidate => candidate.ArtifactId == artifact.ArtifactId, cancellationToken);
        if (existingArtifact is not null)
        {
            var belongsToLocation = existingArtifact.Locations.Any(candidate =>
                string.Equals(candidate.NormalizedPath, location.NormalizedPath, StringComparison.Ordinal));
            if (!belongsToLocation || !string.Equals(existingArtifact.Sha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new CaptureCatalogRegistration(existingArtifact.ArtifactId, false, true);
            }
        }

        if (existingLocation?.CurrentArtifact is { } supersededArtifact)
        {
            supersededArtifact.Availability = CaptureArtifactAvailability.Superseded;
            supersededArtifact.UpdatedAtUtc = artifact.UpdatedAtUtc;
        }

        if (existingArtifact is null)
        {
            await _context.CaptureArtifacts.AddAsync(artifact, cancellationToken);
            existingArtifact = artifact;
        }

        if (existingLocation is null)
        {
            location.CurrentArtifact = existingArtifact;
            await _context.CaptureLocations.AddAsync(location, cancellationToken);
        }
        else
        {
            existingLocation.OriginalPath = location.OriginalPath;
            existingLocation.CurrentArtifact = existingArtifact;
            existingLocation.LastWriteAtUtc = location.LastWriteAtUtc;
            existingLocation.ByteLength = location.ByteLength;
            existingLocation.LastObservedAtUtc = location.LastObservedAtUtc;
        }

        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CaptureCatalogRegistration(existingArtifact.ArtifactId, false, false);
    }

    public async Task<IReadOnlyList<CaptureLocationEntry>> GetLocationsUnderRoot(
        string normalizedRoot,
        CancellationToken cancellationToken = default)
    {
        var rootPrefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : $"{normalizedRoot}{Path.DirectorySeparatorChar}";
        return await _context.CaptureLocations
            .Include(location => location.CurrentArtifact)
            .Where(location => location.NormalizedPath.StartsWith(rootPrefix))
            .ToListAsync(cancellationToken);
    }

    public async Task MarkLocationsMissing(
        IReadOnlyCollection<string> normalizedPaths,
        DateTime observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (normalizedPaths.Count == 0)
        {
            return;
        }

        var locations = await _context.CaptureLocations
            .Include(location => location.CurrentArtifact)
            .Where(location => normalizedPaths.Contains(location.NormalizedPath))
            .ToListAsync(cancellationToken);
        foreach (var location in locations)
        {
            if (location.CurrentArtifact is not null)
            {
                location.CurrentArtifact.Availability = CaptureArtifactAvailability.Missing;
                location.CurrentArtifact.UpdatedAtUtc = observedAtUtc;
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
    }
}
