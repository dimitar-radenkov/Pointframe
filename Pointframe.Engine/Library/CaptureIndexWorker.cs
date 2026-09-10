using System.Drawing;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pointframe.Data.Context;
using Pointframe.Data.Entities;

namespace Pointframe.Engine;

public sealed class CaptureIndexWorker : ICaptureIndexWorker
{
    private const int BatchSize = 20;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ChangedFileRetryDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOcrEngineService _ocrEngine;
    private readonly TimeProvider _timeProvider;

    public CaptureIndexWorker(IServiceScopeFactory scopeFactory, IOcrEngineService ocrEngine, TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _ocrEngine = ocrEngine;
        _timeProvider = timeProvider;
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var owner = $"indexer_{Guid.NewGuid():N}";
        var workItems = await ClaimAsync(owner, cancellationToken).ConfigureAwait(false);
        foreach (var workItem in workItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await IndexAsync(workItem, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RunUntilCancelledAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // A transient database or OCR failure must not stop the long-lived worker.
            }

            await Task.Delay(PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<WorkItem>> ClaimAsync(string owner, CancellationToken cancellationToken)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var leaseExpiresUtc = nowUtc.Add(LeaseDuration);
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PointframeDataContext>();

        await context.CaptureArtifacts
            .Where(artifact => artifact.OcrStatus == CaptureOcrStatus.Processing &&
                artifact.LeaseExpiresUtc != null && artifact.LeaseExpiresUtc <= nowUtc)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(artifact => artifact.OcrStatus, CaptureOcrStatus.Pending)
                .SetProperty(artifact => artifact.LeaseOwner, (string?)null)
                .SetProperty(artifact => artifact.LeaseExpiresUtc, (DateTime?)null)
                .SetProperty(artifact => artifact.NextAttemptUtc, nowUtc)
                .SetProperty(artifact => artifact.UpdatedAtUtc, nowUtc), cancellationToken)
            .ConfigureAwait(false);

        var candidates = await context.CaptureArtifacts
            .Where(artifact => artifact.Availability == CaptureArtifactAvailability.Available &&
                artifact.OcrStatus == CaptureOcrStatus.Pending &&
                (artifact.NextAttemptUtc == null || artifact.NextAttemptUtc <= nowUtc))
            .OrderBy(artifact => artifact.NextAttemptUtc)
            .ThenBy(artifact => artifact.CapturedAtUtc)
            .ThenBy(artifact => artifact.ArtifactId)
            .Select(artifact => new { artifact.ArtifactId, artifact.Sha256 })
            .Take(BatchSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var claimed = new List<WorkItem>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var changed = await context.CaptureArtifacts
                .Where(artifact => artifact.ArtifactId == candidate.ArtifactId &&
                    artifact.Sha256 == candidate.Sha256 &&
                    artifact.Availability == CaptureArtifactAvailability.Available &&
                    artifact.OcrStatus == CaptureOcrStatus.Pending &&
                    (artifact.NextAttemptUtc == null || artifact.NextAttemptUtc <= nowUtc))
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(artifact => artifact.OcrStatus, CaptureOcrStatus.Processing)
                    .SetProperty(artifact => artifact.LeaseOwner, owner)
                    .SetProperty(artifact => artifact.LeaseExpiresUtc, leaseExpiresUtc)
                    .SetProperty(artifact => artifact.AttemptCount, artifact => artifact.AttemptCount + 1)
                    .SetProperty(artifact => artifact.UpdatedAtUtc, nowUtc), cancellationToken)
                .ConfigureAwait(false);
            if (changed == 1)
            {
                claimed.Add(new WorkItem(candidate.ArtifactId, candidate.Sha256, owner));
            }
        }

        return claimed;
    }

    private async Task IndexAsync(WorkItem workItem, CancellationToken cancellationToken)
    {
        var path = await GetCurrentPathAsync(workItem, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(path))
        {
            await ReleaseChangedFileAsync(workItem, cancellationToken).ConfigureAwait(false);
            return;
        }

        OcrRecognitionResult result;
        string? snapshotSha256;
        try
        {
            (result, snapshotSha256) = await RecognizeSnapshotAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            result = new OcrRecognitionResult(null, OcrRecognitionStatus.Failed);
            snapshotSha256 = workItem.Sha256;
        }

        if (!string.Equals(snapshotSha256, workItem.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            await ReleaseChangedFileAsync(workItem, cancellationToken).ConfigureAwait(false);
            return;
        }

        await CompleteAsync(workItem, result, snapshotSha256, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> GetCurrentPathAsync(WorkItem workItem, CancellationToken cancellationToken)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PointframeDataContext>();
        return await context.CaptureLocations
            .Where(location => location.CurrentArtifactId == workItem.ArtifactId &&
                location.CurrentArtifact!.Availability == CaptureArtifactAvailability.Available &&
                location.CurrentArtifact.OcrStatus == CaptureOcrStatus.Processing &&
                location.CurrentArtifact.LeaseOwner == workItem.Owner &&
                location.CurrentArtifact.LeaseExpiresUtc > nowUtc &&
                location.CurrentArtifact.Sha256 == workItem.Sha256)
            .Select(location => location.OriginalPath)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<(OcrRecognitionResult Result, string SnapshotSha256)> RecognizeSnapshotAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var bytes = new MemoryStream();
        await file.CopyToAsync(bytes, cancellationToken).ConfigureAwait(false);
        var snapshot = bytes.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(snapshot));
        using var imageStream = new MemoryStream(snapshot, writable: false);
        using var source = new Bitmap(imageStream);
        using var bitmap = new Bitmap(source);
        return (await _ocrEngine.RecognizeDetailedAsync(bitmap, cancellationToken).ConfigureAwait(false), sha256);
    }

    private async Task ReleaseChangedFileAsync(WorkItem workItem, CancellationToken cancellationToken)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PointframeDataContext>();
        await context.CaptureArtifacts
            .Where(artifact => artifact.ArtifactId == workItem.ArtifactId && artifact.Sha256 == workItem.Sha256 &&
                artifact.OcrStatus == CaptureOcrStatus.Processing && artifact.LeaseOwner == workItem.Owner)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(artifact => artifact.OcrStatus, CaptureOcrStatus.Pending)
                .SetProperty(artifact => artifact.LeaseOwner, (string?)null)
                .SetProperty(artifact => artifact.LeaseExpiresUtc, (DateTime?)null)
                .SetProperty(artifact => artifact.NextAttemptUtc, nowUtc.Add(ChangedFileRetryDelay))
                .SetProperty(artifact => artifact.UpdatedAtUtc, nowUtc), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task CompleteAsync(
        WorkItem workItem,
        OcrRecognitionResult result,
        string? snapshotSha256,
        CancellationToken cancellationToken)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PointframeDataContext>();
        var artifact = await context.CaptureArtifacts.SingleOrDefaultAsync(
            candidate => candidate.ArtifactId == workItem.ArtifactId,
            cancellationToken).ConfigureAwait(false);
        var remainsCurrent = await context.CaptureLocations.AnyAsync(
            location => location.CurrentArtifactId == workItem.ArtifactId,
            cancellationToken).ConfigureAwait(false);
        if (artifact is null ||
            artifact.Availability != CaptureArtifactAvailability.Available ||
            artifact.OcrStatus != CaptureOcrStatus.Processing ||
            artifact.LeaseOwner != workItem.Owner ||
            artifact.LeaseExpiresUtc <= nowUtc ||
            !remainsCurrent ||
            !string.Equals(artifact.Sha256, workItem.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshotSha256, workItem.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        artifact.OcrStatus = result.Status switch
        {
            OcrRecognitionStatus.Recognized => CaptureOcrStatus.Ready,
            OcrRecognitionStatus.NoText => CaptureOcrStatus.NoText,
            OcrRecognitionStatus.Unavailable => CaptureOcrStatus.Unavailable,
            OcrRecognitionStatus.Unsupported => CaptureOcrStatus.Unsupported,
            _ => CaptureOcrStatus.Failed,
        };
        artifact.OcrText = result.Text;
        artifact.SearchTextNormalized = result.Text?.ToUpperInvariant();
        artifact.IndexedSha256 = snapshotSha256;
        artifact.IndexedAtUtc = nowUtc;
        artifact.LeaseOwner = null;
        artifact.LeaseExpiresUtc = null;
        artifact.UpdatedAtUtc = nowUtc;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record WorkItem(string ArtifactId, string Sha256, string Owner);
}
