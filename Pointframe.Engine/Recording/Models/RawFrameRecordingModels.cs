namespace Pointframe.Engine;

public sealed record RawFrameRecordingOptions(
    PixelBounds CaptureBoundsPixels,
    int FramesPerSecond,
    Func<ReadOnlyMemory<PixelBounds>> RedactionRegionsProvider,
    int BufferPoolSize = 4);

public sealed record RawFrameRecordingStatistics(
    int AttemptedFrameCount,
    int WrittenFrameCount,
    int DroppedFrameCount,
    TimeSpan? FirstFrameWriteDelay);
