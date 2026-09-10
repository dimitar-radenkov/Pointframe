using System.Drawing;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pointframe.Data.Abstractions;
using Pointframe.Data.Context;
using Pointframe.Data.Entities;

namespace Pointframe.Engine;

public sealed class CaptureCatalogService : ICaptureCatalogService
{
    private static readonly SemaphoreSlim InitializationGate = new(1, 1);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private Task? _initializationTask;

    public CaptureCatalogService(IServiceScopeFactory scopeFactory, TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
    }

    public async Task<CaptureRegistrationResult> RegisterAsync(
        CaptureRegistrationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var inspected = await InspectAsync(request.Path, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(request.ExpectedSha256) &&
            !string.Equals(request.ExpectedSha256, inspected.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The image changed after its metadata sidecar was read.");
        }
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var artifactId = string.IsNullOrWhiteSpace(request.ArtifactId)
            ? $"img_{Guid.NewGuid():N}"
            : request.ArtifactId;
        var artifact = new CaptureArtifactEntry
        {
            ArtifactId = artifactId,
            Kind = "screenshot",
            MimeType = inspected.MimeType,
            FileName = Path.GetFileName(inspected.FullPath),
            Sha256 = inspected.Sha256,
            ByteLength = inspected.ByteLength,
            PixelWidth = inspected.PixelWidth,
            PixelHeight = inspected.PixelHeight,
            CapturedAtUtc = request.CapturedAtUtc.UtcDateTime,
            TimestampSource = ParseTimestampSource(request.TimestampSource),
            Source = ParseSource(request.Source),
            ProvenanceJson = request.ProvenanceJson,
            Availability = CaptureArtifactAvailability.Available,
            OcrStatus = CaptureOcrStatus.Pending,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };
        var location = new CaptureLocationEntry
        {
            NormalizedPath = NormalizePath(inspected.FullPath),
            OriginalPath = inspected.FullPath,
            LastWriteAtUtc = inspected.LastWriteAtUtc,
            ByteLength = inspected.ByteLength,
            LastObservedAtUtc = nowUtc,
        };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var catalog = scope.ServiceProvider.GetRequiredService<ICaptureCatalogRepository>();
                var registration = await catalog.RegisterObservedArtifact(artifact, location, cancellationToken).ConfigureAwait(false);
                if (registration.IdentityConflict)
                {
                    artifact.ArtifactId = $"img_{Guid.NewGuid():N}";
                    continue;
                }

                return new CaptureRegistrationResult(registration.ArtifactId, registration.AlreadyRegistered);
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // A simultaneous import may have won the unique path race. Re-read through a new scoped context.
            }
        }

        throw new InvalidOperationException("The capture catalog could not register the saved image.");
    }

    public async Task<CaptureCatalogSearchResult> SearchAsync(
        CaptureCatalogSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Limit is < 1 or > 100 || request.FromUtc > request.ToUtc)
        {
            throw new ArgumentException("The capture search request is invalid.", nameof(request));
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PointframeDataContext>();
        var query = request.Query?.Trim() ?? string.Empty;
        var pattern = $"%{EscapeLike(query)}%";
        var candidates = await context.CaptureArtifacts
            .Where(artifact => artifact.Availability == CaptureArtifactAvailability.Available)
            .Where(artifact => request.FromUtc == null || artifact.CapturedAtUtc >= request.FromUtc.Value.UtcDateTime)
            .Where(artifact => request.ToUtc == null || artifact.CapturedAtUtc < request.ToUtc.Value.UtcDateTime)
            .Where(artifact => string.IsNullOrEmpty(query) ||
                EF.Functions.Like(artifact.FileName, pattern, "\\") ||
                (artifact.OcrText != null && EF.Functions.Like(artifact.OcrText, pattern, "\\")))
            .OrderByDescending(artifact => artifact.CapturedAtUtc).ThenByDescending(artifact => artifact.ArtifactId)
            .Take(request.Limit)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var items = candidates
            .Select(artifact => new CaptureCatalogSearchItem(
                artifact.ArtifactId, artifact.FileName, new DateTimeOffset(artifact.CapturedAtUtc),
                artifact.TimestampSource.ToString(), artifact.Source.ToString(), artifact.MimeType,
                artifact.PixelWidth, artifact.PixelHeight, artifact.Sha256, artifact.OcrStatus.ToString(),
                GetMatchedFields(artifact, query), CreateSnippet(artifact.OcrText, query)))
            .ToArray();
        return new CaptureCatalogSearchResult(items, null, new CaptureCatalogIndexState(false, 0, 0, 0, null));
    }

    public async Task<CaptureCatalogArtifact?> GetAsync(string artifactId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ICaptureCatalogRepository>();
        var artifact = await repository.GetArtifactById(artifactId, cancellationToken).ConfigureAwait(false);
        if (artifact is null)
        {
            return null;
        }

        var path = artifact.Locations.FirstOrDefault(location => location.CurrentArtifactId == artifact.ArtifactId)?.OriginalPath;
        return new CaptureCatalogArtifact(artifact.ArtifactId, artifact.FileName, artifact.Sha256, artifact.MimeType,
            artifact.ByteLength, artifact.PixelWidth, artifact.PixelHeight, new DateTimeOffset(artifact.CapturedAtUtc),
            artifact.Availability.ToString(), artifact.OcrStatus.ToString(), artifact.ProvenanceJson, path);
    }

    private static IReadOnlyList<string> GetMatchedFields(CaptureArtifactEntry artifact, string query) =>
        string.IsNullOrEmpty(query) ? [] : [.. new[] { artifact.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) ? "fileName" : null, artifact.OcrText?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ? "ocrText" : null }.OfType<string>()];

    private static string? CreateSnippet(string? text, string query)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var index = string.IsNullOrEmpty(query) ? 0 : text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        index = Math.Max(0, index);
        return text.Substring(index, Math.Min(240, text.Length - index));
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    internal static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
    }

    private Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        lock (this)
        {
            _initializationTask ??= InitializeAsync();
        }

        return _initializationTask.WaitAsync(cancellationToken);
    }

    private async Task InitializeAsync()
    {
        await InitializationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<PointframeDataContext>();
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }
        finally
        {
            InitializationGate.Release();
        }
    }

    private static async Task<InspectedImage> InspectAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("The saved image no longer exists.", fullPath);
        }

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        stream.Position = 0;
        using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
        return new InspectedImage(
            fullPath,
            sha256,
            stream.Length,
            image.Width,
            image.Height,
            GetMimeType(image),
            fileInfo.LastWriteTimeUtc);
    }

    private static string GetMimeType(Image image)
    {
        if (image.RawFormat.Guid == System.Drawing.Imaging.ImageFormat.Png.Guid)
        {
            return "image/png";
        }

        if (image.RawFormat.Guid == System.Drawing.Imaging.ImageFormat.Jpeg.Guid)
        {
            return "image/jpeg";
        }

        if (image.RawFormat.Guid == System.Drawing.Imaging.ImageFormat.Bmp.Guid)
        {
            return "image/bmp";
        }

        throw new NotSupportedException("Only PNG, JPEG, and BMP screenshots can be indexed.");
    }

    private static CaptureTimestampSource ParseTimestampSource(string value) => value switch
    {
        "capture" => CaptureTimestampSource.Capture,
        "sidecar" => CaptureTimestampSource.Sidecar,
        "file_last_write" => CaptureTimestampSource.FileLastWrite,
        _ => throw new ArgumentException("The timestamp source is invalid.", nameof(value)),
    };

    private static CaptureArtifactSource ParseSource(string value) => value switch
    {
        "wpf_save" => CaptureArtifactSource.WpfSave,
        "wpf_save_as" => CaptureArtifactSource.WpfSaveAs,
        "wpf_auto_save" => CaptureArtifactSource.WpfAutoSave,
        "wpf_beautifier" => CaptureArtifactSource.WpfBeautifier,
        "direct_monitor" => CaptureArtifactSource.DirectMonitor,
        "direct_window" => CaptureArtifactSource.DirectWindow,
        "import" => CaptureArtifactSource.Import,
        _ => throw new ArgumentException("The capture source is invalid.", nameof(value)),
    };

    private sealed record InspectedImage(
        string FullPath,
        string Sha256,
        long ByteLength,
        int PixelWidth,
        int PixelHeight,
        string MimeType,
        DateTime LastWriteAtUtc);
}
