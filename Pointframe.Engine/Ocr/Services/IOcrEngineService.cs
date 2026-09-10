namespace Pointframe.Engine;

public interface IOcrEngineService
{
    Task<string?> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken = default);

    async Task<OcrRecognitionResult> RecognizeDetailedAsync(Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        var text = await RecognizeAsync(bitmap, cancellationToken).ConfigureAwait(false);
        return text is null ? OcrRecognitionResult.Unavailable : OcrRecognitionResult.Recognized(text);
    }
}

public sealed record OcrRecognitionResult(string? Text, OcrRecognitionStatus Status)
{
    public static OcrRecognitionResult Recognized(string text) => new(text, OcrRecognitionStatus.Recognized);

    public static OcrRecognitionResult NoText { get; } = new(null, OcrRecognitionStatus.NoText);

    public static OcrRecognitionResult Unavailable { get; } = new(null, OcrRecognitionStatus.Unavailable);
}

public enum OcrRecognitionStatus { Recognized, NoText, Unavailable, Failed, Unsupported }
