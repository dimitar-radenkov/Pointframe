using System.Text;

namespace Pointframe.Services;

public sealed class TranscriptionService : ITranscriptionService
{
    private readonly IAudioExtractor _audioExtractor;
    private readonly ISpeechRecognizer _speechRecognizer;
    private readonly ITranscriptModelService _modelService;
    // Encoding.UTF8 emits a byte-order mark; an SRT file must start with the cue
    // index or players drop the first subtitle.
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly ILogger<TranscriptionService> _logger;

    public TranscriptionService(
        IAudioExtractor audioExtractor,
        ISpeechRecognizer speechRecognizer,
        ITranscriptModelService modelService,
        ILogger<TranscriptionService> logger)
    {
        _audioExtractor = audioExtractor;
        _speechRecognizer = speechRecognizer;
        _modelService = modelService;
        _logger = logger;
    }

    public async Task<TranscriptionResult> TranscribeVideoAsync(string videoPath, CancellationToken ct)
    {
        var modelPath = _modelService.ResolveModelPath();
        if (modelPath is null)
        {
            _logger.LogInformation("Transcription skipped: Whisper model not found");
            return new TranscriptionResult(false, null, null, TranscriptionSkipReasons.ModelNotFound, null);
        }

        string? tempWavPath = null;
        try
        {
            tempWavPath = await _audioExtractor.ExtractWavAsync(videoPath, ct).ConfigureAwait(false);

            var segments = new List<TranscriptSegment>();
            await foreach (var segment in _speechRecognizer.TranscribeAsync(tempWavPath, modelPath, ct).ConfigureAwait(false))
            {
                segments.Add(segment);
            }

            // Whisper emits empty or whitespace-only segments for silence, so a
            // non-zero segment count does not mean anything was actually said.
            if (!SubtitleFormatter.HasSpeech(segments))
            {
                _logger.LogInformation("Transcription produced no speech for {Video}", videoPath);
                return new TranscriptionResult(false, null, null, TranscriptionSkipReasons.NoSpeechDetected, null);
            }

            var srtPath = Path.ChangeExtension(videoPath, ".srt");
            var txtPath = Path.ChangeExtension(videoPath, ".txt");

            await File.WriteAllTextAsync(srtPath, SubtitleFormatter.FormatSrt(segments), Utf8WithoutBom, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(txtPath, SubtitleFormatter.FormatPlainText(segments), Utf8WithoutBom, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Transcription complete: {Segments} segments → {Srt}, {Txt}",
                segments.Count, srtPath, txtPath);

            return new TranscriptionResult(true, srtPath, txtPath, null, null, segments.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Transcription cancelled for {Video}", videoPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transcription failed for {Video}", videoPath);
            return new TranscriptionResult(false, null, null, null, ex.Message);
        }
        finally
        {
            DeleteIfExists(tempWavPath);
        }
    }

    private void DeleteIfExists(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete temp WAV file {Path}", path);
        }
    }
}
