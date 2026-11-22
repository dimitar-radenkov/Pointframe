namespace Pointframe.Services;

public interface IVideoWriter : IDisposable
{
    void WriteFrame(byte[] frameData);
}

public interface IVideoWriterReadiness
{
    bool WaitUntilReady(TimeSpan timeout);

    double? AudioStartQpcSeconds => null;
}

public interface IRecordingFailureDiagnostics
{
    int? FfmpegExitCode { get; }
    string RecentStandardError { get; }
    string? FailureReason { get; }
}

public interface IRecoverableVideoWriter
{
    string IntermediatePath { get; }
}
