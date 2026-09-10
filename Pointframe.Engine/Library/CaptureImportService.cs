using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pointframe.Data.Abstractions;

namespace Pointframe.Engine;

public sealed class CaptureImportService : ICaptureImportService
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp" };
    private readonly ICaptureCatalogService _catalogService;
    private readonly ICaptureLibrarySources _sources;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _reconciliationGate = new(1, 1);

    public CaptureImportService(
        ICaptureCatalogService catalogService,
        ICaptureLibrarySources sources,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider)
    {
        _catalogService = catalogService;
        _sources = sources;
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
    }

    public async Task RequestReconciliationAsync(CancellationToken cancellationToken = default)
    {
        if (!await _reconciliationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            foreach (var root in _sources.GetImportRoots())
            {
                await ReconcileRootAsync(root, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _reconciliationGate.Release();
        }
    }

    private async Task ReconcileRootAsync(string root, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        var observedPaths = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SupportedExtensions.Contains(Path.GetExtension(path)))
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(path);
                observedPaths.Add(CaptureCatalogService.NormalizePath(fullPath));
                try
                {
                    await RegisterImportedFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // The file was observed during a complete enumeration. A sharing race means its
                    // existing generation remains observed and will be retried next reconciliation.
                }
                catch (UnauthorizedAccessException)
                {
                    // See the IOException case above.
                }
            }
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ICaptureCatalogRepository>();
        var existingLocations = await repository.GetLocationsUnderRoot(
            CaptureCatalogService.NormalizePath(root), cancellationToken).ConfigureAwait(false);
        var missingPaths = existingLocations
            .Select(location => location.NormalizedPath)
            .Where(path => !observedPaths.Contains(path))
            .ToArray();
        await repository.MarkLocationsMissing(
            missingPaths,
            _timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RegisterImportedFileAsync(string path, CancellationToken cancellationToken)
    {
        var sidecar = await ReadSidecarAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            await _catalogService.RegisterAsync(new CaptureRegistrationRequest(
                path,
                sidecar?.ArtifactId,
                sidecar is null ? "import" : ToSource(sidecar.Source),
                sidecar?.CreatedUtc ?? File.GetLastWriteTimeUtc(path),
                sidecar is null ? "file_last_write" : "sidecar",
                ExpectedSha256: sidecar?.Sha256), cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException) when (sidecar is not null)
        {
            await _catalogService.RegisterAsync(new CaptureRegistrationRequest(
                Path: path,
                ArtifactId: null,
                Source: "import",
                CapturedAtUtc: File.GetLastWriteTimeUtc(path),
                TimestampSource: "file_last_write"), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<ImageArtifactMetadata?> ReadSidecarAsync(string imagePath, CancellationToken cancellationToken)
    {
        var sidecarPath = $"{imagePath}.metadata.json";
        if (!File.Exists(sidecarPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
            var metadata = JsonSerializer.Deserialize<ImageArtifactMetadata>(json);
            if (metadata is null ||
                !string.Equals(Path.GetFullPath(metadata.Path), Path.GetFullPath(imagePath), StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(metadata.ArtifactId) ||
                string.IsNullOrWhiteSpace(metadata.Sha256))
            {
                return null;
            }

            await using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            return string.Equals(hash, metadata.Sha256, StringComparison.OrdinalIgnoreCase) ? metadata : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ToSource(string source) => source switch
    {
        "direct-monitor-capture" => "direct_monitor",
        "direct-window-capture" => "direct_window",
        _ => "import",
    };
}
