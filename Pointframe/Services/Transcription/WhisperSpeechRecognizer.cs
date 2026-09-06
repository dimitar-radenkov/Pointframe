using Whisper.net;

namespace Pointframe.Services;

public sealed class WhisperSpeechRecognizer : ISpeechRecognizer
{
    private readonly ILogger<WhisperSpeechRecognizer> _logger;

    public WhisperSpeechRecognizer(ILogger<WhisperSpeechRecognizer> logger)
    {
        _logger = logger;
    }

    public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
        string wavPath,
        string modelPath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        _logger.LogInformation("Loading Whisper model from {Model}", modelPath);

        using var factory = WhisperFactory.FromPath(modelPath);
        using var processor = factory.CreateBuilder()
            .WithLanguage("en")
            .Build();

        using var stream = File.OpenRead(wavPath);

        await foreach (var segment in processor.ProcessAsync(stream, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            yield return new TranscriptSegment(segment.Start, segment.End, segment.Text);
        }
    }
}
