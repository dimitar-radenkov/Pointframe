namespace Pointframe.Services;

public interface ISpeechRecognizer
{
    IAsyncEnumerable<TranscriptSegment> TranscribeAsync(string wavPath, string modelPath, CancellationToken ct);
}
