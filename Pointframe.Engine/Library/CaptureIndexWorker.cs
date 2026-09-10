using System.Drawing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pointframe.Data.Context;
using Pointframe.Data.Entities;

namespace Pointframe.Engine;

public sealed class CaptureIndexWorker : ICaptureIndexWorker
{
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
        WorkItem[] workItems;
        using (var scope = _scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PointframeDataContext>();
            var candidates = await context.CaptureArtifacts
                .Where(artifact => artifact.Availability == CaptureArtifactAvailability.Available && artifact.OcrStatus == CaptureOcrStatus.Pending)
                .OrderBy(artifact => artifact.CapturedAtUtc).Take(20).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var candidate in candidates)
            {
                candidate.OcrStatus = CaptureOcrStatus.Processing;
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            workItems = candidates.Select(candidate => new WorkItem(candidate.ArtifactId, candidate.Sha256)).ToArray();
        }

        foreach (var workItem in workItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await IndexAsync(workItem, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task IndexAsync(WorkItem workItem, CancellationToken cancellationToken)
    {
        string? path;
        using (var scope = _scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PointframeDataContext>();
            path = await context.CaptureLocations.Where(location => location.CurrentArtifactId == workItem.ArtifactId)
                .Select(location => location.OriginalPath).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        OcrRecognitionResult result;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                result = new(null, OcrRecognitionStatus.Failed);
            }
            else { using var bitmap = new Bitmap(path); result = await _ocrEngine.RecognizeDetailedAsync(bitmap, cancellationToken).ConfigureAwait(false); }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { result = new(null, OcrRecognitionStatus.Failed); }

        using var completionScope = _scopeFactory.CreateScope();
        var completionContext = completionScope.ServiceProvider.GetRequiredService<PointframeDataContext>();
        var artifact = await completionContext.CaptureArtifacts.SingleOrDefaultAsync(item => item.ArtifactId == workItem.ArtifactId, cancellationToken).ConfigureAwait(false);
        var remainsCurrent = await completionContext.CaptureLocations.AnyAsync(
            location => location.CurrentArtifactId == workItem.ArtifactId,
            cancellationToken).ConfigureAwait(false);
        if (artifact is null ||
            artifact.OcrStatus != CaptureOcrStatus.Processing ||
            artifact.Availability != CaptureArtifactAvailability.Available ||
            !remainsCurrent ||
            !string.Equals(artifact.Sha256, workItem.Sha256, StringComparison.OrdinalIgnoreCase))
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
        artifact.IndexedSha256 = artifact.Sha256;
        artifact.IndexedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        artifact.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        await completionContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record WorkItem(string ArtifactId, string Sha256);
}
