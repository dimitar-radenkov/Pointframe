using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pointframe.Services;

public sealed class ShareService : IShareService
{
    private const int MaximumResponseBytes = 65_536;
    internal static HttpClient SharedHttpClient { get; } = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };
    private readonly HttpClient _httpClient;
    private readonly IUserSettingsService _settings;
    private readonly ILogger<ShareService> _logger;

    public ShareService(HttpClient httpClient, IUserSettingsService settings, ILogger<ShareService> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _logger = logger;
    }

    public async Task<ShareResult> UploadAsync(byte[] pngBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        var settings = _settings.Current;
        if (string.IsNullOrWhiteSpace(settings.ShareDestinationUrl))
        {
            return ShareResult.Failed(ShareFailure.NotConfigured);
        }

        if (!TryGetDestination(settings.ShareDestinationUrl, out var destination))
        {
            return ShareResult.Failed(ShareFailure.InvalidUrl);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(settings.ShareTimeoutSeconds, 1, 300)));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, destination);
        using var multipart = new MultipartFormDataContent();
        var imageContent = new ByteArrayContent(pngBytes);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        try
        {
            multipart.Add(imageContent, settings.ShareFileFieldName, "capture.png");
            request.Content = multipart;
            foreach (var header in settings.ShareHeaders ?? [])
            {
                var value = ShareHeaderProtection.Unprotect(header);
                if (!request.Headers.TryAddWithoutValidation(header.Name, value)
                    && !multipart.Headers.TryAddWithoutValidation(header.Name, value))
                {
                    return ShareResult.Failed(ShareFailure.RequestFailed);
                }
            }

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linkedCancellation.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Share upload returned HTTP {StatusCode}", (int)response.StatusCode);
                return ShareResult.Failed(ShareFailure.HttpStatus, (int)response.StatusCode);
            }

            var body = await ReadBoundedBodyAsync(response.Content, linkedCancellation.Token).ConfigureAwait(false);
            if (body is null)
            {
                return ShareResult.Failed(ShareFailure.UnparseableResponse);
            }

            var link = ExtractLink(body, settings.ShareResponseLinkPath);
            if (link is null)
            {
                return ShareResult.Failed(ShareFailure.UnparseableResponse);
            }

            if (!Uri.TryCreate(link, UriKind.Absolute, out var resultUri))
            {
                return ShareResult.Failed(ShareFailure.UnparseableResponse);
            }

            if (resultUri.Scheme != Uri.UriSchemeHttps)
            {
                return ShareResult.Failed(ShareFailure.LinkNotHttps);
            }

            return ShareResult.Success(resultUri.AbsoluteUri);
        }
        catch (OperationCanceledException)
        {
            return ShareResult.Failed(cancellationToken.IsCancellationRequested ? ShareFailure.Cancelled : ShareFailure.Timeout);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or FormatException or CryptographicException or ArgumentException or InvalidOperationException)
        {
            _logger.LogWarning("Share upload failed while processing the request");
            return ShareResult.Failed(ShareFailure.RequestFailed);
        }
    }

    private static bool TryGetDestination(string value, out Uri destination)
    {
        destination = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            destination = uri;
            return true;
        }

        return false;
    }

    private static async Task<string?> ReadBoundedBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return Encoding.UTF8.GetString(output.ToArray());
            }

            if (output.Length + read > MaximumResponseBytes)
            {
                return null;
            }

            output.Write(buffer, 0, read);
        }
    }

    private static string? ExtractLink(string body, string path)
    {
        if (Uri.TryCreate(body.Trim(), UriKind.Absolute, out var plainUri))
        {
            return plainUri.AbsoluteUri;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var current = document.RootElement;
            foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.ValueKind == JsonValueKind.Array)
                {
                    if (!int.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
                        || index >= current.GetArrayLength())
                    {
                        return null;
                    }

                    current = current[index];
                }
                else if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                {
                    return null;
                }
            }

            return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
