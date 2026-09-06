namespace Pointframe.Models;

public sealed record TranscriptionResult(
    bool Success,
    string? SrtPath,
    string? TxtPath,
    string? SkipReason,
    string? ErrorMessage,
    int SegmentCount = 0);

public static class TranscriptionSkipReasons
{
    public const string ModelNotFound = "Whisper model not found";
    public const string NoSpeechDetected = "No speech detected";
}
