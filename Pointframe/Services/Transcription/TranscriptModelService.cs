using System.Net.Http;

namespace Pointframe.Services;

public sealed class TranscriptModelService : ITranscriptModelService
{
    private const string ModelDownloadUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin";

    // Size of ggml-base.en.bin. Used to show the cost up front and to reject a
    // truncated or error-page response before it is promoted to the real file.
    private const long ExpectedBytes = 147_964_211;

    private readonly HttpClient _http;
    private readonly ILogger<TranscriptModelService> _logger;

    public TranscriptModelService(HttpClient http, ILogger<TranscriptModelService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public bool IsModelInstalled => ResolveModelPath() is not null;

    public string? ResolveModelPath() => TranscriptModelResolver.ResolveModelPath();

    public long ExpectedDownloadBytes => ExpectedBytes;

    public async Task<bool> DownloadModel(IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        var destinationPath = TranscriptModelResolver.UserModelPath;
        var partialPath = destinationPath + ".part";

        try
        {
            Directory.CreateDirectory(TranscriptModelResolver.UserModelDirectory);

            using var response = await _http
                .GetAsync(ModelDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? ExpectedBytes;
            var buffer = new byte[81920];
            var downloaded = 0L;

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = File.Create(partialPath))
            {
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    downloaded += read;

                    if (totalBytes > 0)
                    {
                        progress?.Report(downloaded * 100.0 / totalBytes);
                    }
                }
            }

            if (downloaded < ExpectedBytes)
            {
                _logger.LogError(
                    "Model download was truncated: got {Downloaded} bytes, expected {Expected}",
                    downloaded, ExpectedBytes);
                DeleteIfExists(partialPath);
                return false;
            }

            // Promote only after a complete download so a cancelled or failed
            // attempt can never leave a half-written file that resolves as installed.
            File.Move(partialPath, destinationPath, overwrite: true);
            progress?.Report(100);
            _logger.LogInformation("Speech model downloaded to {Path}", destinationPath);
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Speech model download cancelled");
            DeleteIfExists(partialPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Speech model download failed");
            DeleteIfExists(partialPath);
            return false;
        }
    }

    private void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete partial model download {Path}", path);
        }
    }
}
