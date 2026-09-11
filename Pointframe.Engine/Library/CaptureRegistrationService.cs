using System.Security.Cryptography;
using System.Text.Json;

namespace Pointframe.Engine;

public sealed class CaptureRegistrationService : ICaptureRegistrationService
{
    private readonly ICaptureCatalogService _catalog;
    private readonly string _receiptDirectory;

    public CaptureRegistrationService(ICaptureCatalogService catalog, string? receiptDirectory = null)
    {
        _catalog = catalog;
        _receiptDirectory = receiptDirectory ?? Path.Combine(PointframePaths.LocalAppDataDirectory, "registration-receipts");
    }

    public async Task RegisterOrQueueAsync(CaptureRegistrationRequest request, CancellationToken cancellationToken = default)
    {
        var verifiedRequest = WithVerifiedHash(request, cancellationToken);
        try
        {
            await _catalog.RegisterAsync(verifiedRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            await WriteReceiptAsync(verifiedRequest, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ReplayPendingAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_receiptDirectory))
        {
            return;
        }

        foreach (var receiptPath in Directory.EnumerateFiles(_receiptDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var request = JsonSerializer.Deserialize<CaptureRegistrationRequest>(
                    await File.ReadAllTextAsync(receiptPath, cancellationToken).ConfigureAwait(false));
                if (request is null)
                {
                    continue;
                }

                await _catalog.RegisterAsync(request, cancellationToken).ConfigureAwait(false);
                File.Delete(receiptPath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Keep the receipt until a later host can validate and register it.
            }
        }
    }

    private async Task WriteReceiptAsync(CaptureRegistrationRequest request, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_receiptDirectory);
        var path = Path.Combine(_receiptDirectory, $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(request), cancellationToken).ConfigureAwait(false);
    }

    private static CaptureRegistrationRequest WithVerifiedHash(CaptureRegistrationRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.ExpectedSha256))
        {
            return request;
        }

        using var stream = new FileStream(request.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        cancellationToken.ThrowIfCancellationRequested();
        return request with { ExpectedSha256 = hash };
    }
}
