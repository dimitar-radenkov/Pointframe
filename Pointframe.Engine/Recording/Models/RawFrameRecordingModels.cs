namespace Pointframe.Engine;

public sealed record RawFrameRecordingOptions(
    PixelBounds CaptureBoundsPixels,
    int FramesPerSecond,
    Func<ReadOnlyMemory<PixelBounds>> RedactionRegionsProvider,
    int BufferPoolSize = 4,
    Action? FirstFrameCaptured = null,
    Action<TimeSpan, TimeSpan>? FrameDropped = null);

public sealed record RawFrameRecordingStatistics(
    int AttemptedFrameCount,
    int WrittenFrameCount,
    int DroppedFrameCount,
    TimeSpan? FirstFrameWriteDelay);
