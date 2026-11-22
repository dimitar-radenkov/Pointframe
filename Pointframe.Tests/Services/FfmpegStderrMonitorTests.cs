using System.IO;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class FfmpegStderrMonitorTests
{
    // Captured from the bundled ffmpeg n9.0.2 (BtbN win64 build) started with the microphone input first and a lavfi
    // source standing in for dshow: "Input #0" is printed as soon as that input is open, before any video frame is read.
    private const string RealStderrFirstInputOpened =
        "ffmpeg version n9.0.2-28-g669f5ab083-20261015 Copyright (c) 2000-2026 the FFmpeg developers\r\n" +
        "  built with gcc 16.2.0 (crosstool-NG 1.29.0.7_b1a94f6)\r\n" +
        "  libavutil      61.  1.102 / 61.  1.102\r\n" +
        "  libswresample   7.  1.102 /  7.  1.102\r\n" +
        "Input #0, lavfi, from 'sine=frequency=440':\r\n" +
        "  Duration: N/A, start: 0.000000, bitrate: 705 kb/s\r\n" +
        "  Stream #0:0: Audio: pcm_s16le, 44100 Hz, mono, s16, 705 kb/s\r\n";

    // Printed only after the first video frame has been probed.
    private const string RealStderrAfterVideoProbe =
        "Input #1, rawvideo, from 'pipe:0':\r\n" +
        "  Duration: N/A, start: 1795158232.400000, bitrate: 1310 kb/s\r\n" +
        "Stream mapping:\r\n" +
        "  Stream #1:0 -> #0:0 (rawvideo (native) -> h264 (libx264))\r\n";

    [Theory]
    [InlineData("Input #0, lavfi, from 'sine=frequency=440':", true)]
    [InlineData("Input #0, dshow, from 'audio=Studio Mic (USB)':", true)]
    [InlineData("Input #1, rawvideo, from 'pipe:0':", false)]
    [InlineData("  Stream #0:0: Audio: pcm_s16le, 44100 Hz, mono, s16, 705 kb/s", false)]
    [InlineData("Stream mapping:", false)]
    [InlineData("", false)]
    public void IsFirstInputOpenedLine_MatchesOnlyTheFirstInputDump(string line, bool expected)
    {
        Assert.Equal(expected, FfmpegStderrMonitor.IsFirstInputOpenedLine(line));
    }

    [Fact]
    public async Task RunAsync_WithRealOutput_CompletesTheSignalTrue()
    {
        var monitor = new FfmpegStderrMonitor(NullLogger.Instance);

        await monitor.RunAsync(new StringReader(RealStderrFirstInputOpened));

        Assert.True(await monitor.FirstInputOpened);
        Assert.Contains("Input #0, lavfi", monitor.RecentLines);
    }

    [Fact]
    public async Task RunAsync_WhenTheInputLineArrivesSplitAcrossReads_StillSignals()
    {
        var monitor = new FfmpegStderrMonitor(NullLogger.Instance);
        using var stream = new ChunkedStream(Encoding.UTF8.GetBytes(RealStderrFirstInputOpened), chunkSize: 3);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var run = monitor.RunAsync(reader);
        await run;

        Assert.True(monitor.FirstInputOpened.IsCompleted);
        Assert.True(await monitor.FirstInputOpened);
    }

    [Fact]
    public async Task RunAsync_BeforeTheInputLine_LeavesTheSignalPending()
    {
        var monitor = new FfmpegStderrMonitor(NullLogger.Instance);
        var pipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
        using var client = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, pipe.ClientSafePipeHandle);
        using var reader = new StreamReader(client, Encoding.UTF8);
        using var writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };

        var run = monitor.RunAsync(reader);
        await writer.WriteAsync("ffmpeg version n9.0.2\r\n  built with gcc\r\n");
        await Task.Delay(100);
        Assert.False(monitor.FirstInputOpened.IsCompleted);

        await writer.WriteAsync("Input #0, dshow, from 'audio=Mic':\r\n");
        Assert.True(await monitor.FirstInputOpened.WaitAsync(TimeSpan.FromSeconds(10)));

        writer.Dispose();
        await run;
    }

    [Fact]
    public async Task RunAsync_WhenStderrEndsWithoutTheInputLine_CompletesTheSignalFalse()
    {
        var monitor = new FfmpegStderrMonitor(NullLogger.Instance);

        await monitor.RunAsync(new StringReader("Error opening input: Immediate exit requested\r\n"));

        Assert.False(await monitor.FirstInputOpened);
    }

    [Fact]
    public async Task RunAsync_ForOutputAfterTheVideoProbe_OnlySignalsOnInputZero()
    {
        var monitor = new FfmpegStderrMonitor(NullLogger.Instance);

        await monitor.RunAsync(new StringReader(RealStderrAfterVideoProbe));

        Assert.False(await monitor.FirstInputOpened);
    }

    [Fact]
    public void Observe_KeepsOnlyTheLastTwelveLines()
    {
        var monitor = new FfmpegStderrMonitor(NullLogger.Instance);

        for (var index = 0; index < 20; index++)
        {
            monitor.Observe($"line {index}");
        }

        var lines = monitor.RecentLines.Split(Environment.NewLine);
        Assert.Equal(12, lines.Length);
        Assert.Equal("line 8", lines[0]);
        Assert.Equal("line 19", lines[^1]);
    }

    private const string RealDshowInputLines =
        "Input #0, dshow, from 'audio=Microphone (USB)':\r\n" +
        "  Duration: N/A, start: 230151.020000, bitrate: 1411 kb/s\r\n" +
        "  Stream #0:0: Audio: pcm_s16le, 44100 Hz, stereo, s16, 1411 kb/s\r\n" +
        "Input #1, rawvideo, from 'pipe:0':\r\n" +
        "  Duration: N/A, start: 1795158232.400000, bitrate: 1310 kb/s\r\n";

    [Fact]
    public async Task RunAsync_ParsesTheFirstInputStartFromTheDurationLine()
    {
        var monitor = new FfmpegStderrMonitor(NullLogger.Instance);

        await monitor.RunAsync(new StringReader(RealDshowInputLines));

        Assert.Equal(230151.02, monitor.FirstInputStartSeconds);
    }

    [Fact]
    public async Task RunAsync_ParsesTheStartWhenReadsSplitTheLines()
    {
        var monitor = new FfmpegStderrMonitor(NullLogger.Instance);
        using var stream = new ChunkedStream(Encoding.UTF8.GetBytes(RealDshowInputLines), chunkSize: 4);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        await monitor.RunAsync(reader);

        Assert.Equal(230151.02, monitor.FirstInputStartSeconds);
    }

    [Theory]
    [InlineData("Input #0, dshow, from 'audio=Mic':\r\n  Duration: N/A, start: N/A, bitrate: N/A\r\n")]
    [InlineData("Input #0, dshow, from 'audio=Mic':\r\n  Stream #0:0: Audio: pcm_s16le\r\n")]
    [InlineData("Input #1, rawvideo, from 'pipe:0':\r\n  Duration: N/A, start: 12.5, bitrate: 1 kb/s\r\n")]
    [InlineData("")]
    public async Task RunAsync_WithoutAStartOnTheFirstInput_LeavesItNull(string text)
    {
        var monitor = new FfmpegStderrMonitor(NullLogger.Instance);

        await monitor.RunAsync(new StringReader(text));

        Assert.Null(monitor.FirstInputStartSeconds);
    }

    private sealed class ChunkedStream(byte[] data, int chunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var length = Math.Min(Math.Min(count, chunkSize), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, length);
            _position += length;
            return length;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
