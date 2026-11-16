namespace Pointframe.Services;

public interface IVideoWriter : IDisposable
{
    void WriteFrame(byte[] frameData);
}

public interface IRecordingFailureDiagnostics
{
    int? FfmpegExitCode { get; }
    string RecentStandardError { get; }
    string? FailureReason { get; }
}

public interface IVideoStartTimeSource
{
    // Epoch seconds of the first video frame ffmpeg read, parsed from its input banner; null when not reported.
    double? InputStartEpochSeconds { get; }
}

public interface IRecoverableVideoWriter
{
    string IntermediatePath { get; }
}
