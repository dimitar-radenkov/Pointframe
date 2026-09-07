namespace Pointframe.Engine;

public interface IDirectRecordingService : IDisposable
{
    DirectRecordingResult Start(DirectRecordingRequest request);

    Task<DirectRecordingResult> StopAsync(CancellationToken cancellationToken = default);
}

public interface IDirectVideoWriter : IDisposable
{
    void WriteFrame(byte[] frameData);
}

public interface IDirectVideoWriterFactory
{
    IDirectVideoWriter Create(int width, int height, int framesPerSecond, string outputPath);
}

public interface IRawFrameWriter
{
    void WriteFrame(byte[] frameData);
}

public interface IRawFrameCapture : IDisposable
{
    void Capture(byte[] frameData);
}
