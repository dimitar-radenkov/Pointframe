using Pointframe.Engine;
using Xunit;

namespace Pointframe.Tests.Engine;

public sealed class RawFrameRecordingPipelineTests
{
    [Fact]
    public void Stop_PadsUsingOneCloneOfTheLatestCapturedFrame()
    {
        var writer = new CollectingWriter();
        using var capture = new StaticFrameCapture([1, 2, 3, 4]);
        using var pipeline = new RawFrameRecordingPipeline(
            writer,
            new RawFrameRecordingOptions(new PixelBounds(0, 0, 1, 1), 10, () => ReadOnlyMemory<PixelBounds>.Empty),
            capture);

        Assert.True(capture.Captured.Wait(TimeSpan.FromSeconds(1)));
        var statistics = pipeline.Stop(TimeSpan.FromMilliseconds(250));

        Assert.True(statistics.WrittenFrameCount >= 3);
        Assert.All(writer.Frames, frame => Assert.Equal(new byte[] { 1, 2, 3, 4 }, frame));
    }

    [Fact]
    public void Pipeline_PixelatesRedactionsBeforeWritingFrames()
    {
        var writer = new CollectingWriter();
        using var pipeline = new RawFrameRecordingPipeline(
            writer,
            new RawFrameRecordingOptions(
                new PixelBounds(0, 0, 16, 2),
                60,
                () => new PixelBounds[] { new(0, 0, 8, 2) }),
            new IndexedFrameCapture());

        Thread.Sleep(75);
        pipeline.Stop(TimeSpan.Zero);

        Assert.NotEmpty(writer.Frames);
        var frame = writer.Frames[0];
        Assert.Equal(GetPixel(frame, 16, 0, 0), GetPixel(frame, 16, 7, 1));
    }

    private static byte[] GetPixel(byte[] frame, int width, int x, int y)
    {
        var offset = ((y * width) + x) * 4;
        return frame[offset..(offset + 4)];
    }

    private sealed class CollectingWriter : IRawFrameWriter
    {
        public List<byte[]> Frames { get; } = [];

        public void WriteFrame(byte[] frameData)
        {
            Frames.Add((byte[])frameData.Clone());
        }
    }

    private sealed class StaticFrameCapture(byte[] frame) : IRawFrameCapture
    {
        public ManualResetEventSlim Captured { get; } = new();

        public void Capture(byte[] frameData)
        {
            frame.CopyTo(frameData, 0);
            Captured.Set();
        }

        public void Dispose()
        {
            Captured.Dispose();
        }
    }

    private sealed class IndexedFrameCapture : IRawFrameCapture
    {
        public void Capture(byte[] frameData)
        {
            for (var offset = 0; offset < frameData.Length; offset += 4)
            {
                frameData[offset] = (byte)(offset / 4);
                frameData[offset + 1] = 0;
                frameData[offset + 2] = 0;
                frameData[offset + 3] = 255;
            }
        }

        public void Dispose()
        {
        }
    }
}