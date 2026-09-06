using System.Drawing;
using System.IO;
using System.Text.Json;
using Pointframe.Engine;
using Xunit;

namespace Pointframe.Tests.Engine;

public sealed class DirectRecordingServiceTests : IDisposable
{
    private readonly string _outputDirectory = Path.Combine(Path.GetTempPath(), $"Pointframe.Tests.{Guid.NewGuid():N}");

    [Fact]
    public async Task StartAndStopAsync_WritesRedactedFramesAndCompatibleSidecars()
    {
        var display = new DisplayDescriptor(
            @"\\.\DISPLAY1",
            1d,
            1d,
            new PixelBounds(0, 0, 16, 2),
            new PixelBounds(0, 0, 16, 1));
        var writerFactory = new FakeVideoWriterFactory();
        using var sut = new DirectRecordingService(new FakeDisplayCaptureEngine(display), writerFactory);

        var start = sut.Start(new DirectRecordingRequest(
            display.MonitorName,
            [new PixelBounds(0, 0, 8, 2)],
            FramesPerSecond: 60,
            OutputDirectory: _outputDirectory));
        await Task.Delay(100);
        var stop = await sut.StopAsync();

        Assert.True(start.Success);
        Assert.True(stop.Success);
        var artifact = Assert.IsType<DirectRecordingArtifact>(stop.Artifact);
        var frame = Assert.Single(writerFactory.Writer.Frames);
        Assert.Equal(GetPixel(frame, 16, 0, 0), GetPixel(frame, 16, 7, 1));
        Assert.True(File.Exists(artifact.Path));
        Assert.True(File.Exists($"{artifact.Path}.metadata.json"));
        Assert.True(File.Exists(artifact.EventSidecarPath));
        Assert.False(artifact.HadMicrophoneAudio);
        Assert.Equal(new PixelBounds(0, 0, 16, 1), artifact.WorkAreaBoundsPixels);
        var persisted = JsonSerializer.Deserialize<DirectRecordingArtifact>(File.ReadAllText($"{artifact.Path}.metadata.json"));
        Assert.Equal(artifact, persisted);
        var events = File.ReadLines(artifact.EventSidecarPath).ToArray();
        Assert.Equal(3, events.Length);
        Assert.Contains("recording.started", events[0], StringComparison.Ordinal);
        Assert.Contains("redaction.added", events[1], StringComparison.Ordinal);
        Assert.Contains("recording.stopped", events[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Start_WithoutAnActiveRedaction_ReturnsAStateError()
    {
        var display = new DisplayDescriptor(@"\\.\DISPLAY1", 1d, 1d, new PixelBounds(0, 0, 16, 2));
        using var sut = new DirectRecordingService(new FakeDisplayCaptureEngine(display), new FakeVideoWriterFactory());

        var result = sut.Start(new DirectRecordingRequest(display.MonitorName, null!, OutputDirectory: _outputDirectory));

        Assert.False(result.Success);
        Assert.Equal("redaction_regions_required", result.ErrorCode);
    }

    [Fact]
    public async Task Start_WhenRecordingIsActive_ReturnsStructuredStateError()
    {
        var display = new DisplayDescriptor(@"\\.\DISPLAY1", 1d, 1d, new PixelBounds(0, 0, 16, 2));
        using var sut = new DirectRecordingService(new FakeDisplayCaptureEngine(display), new FakeVideoWriterFactory());
        var request = new DirectRecordingRequest(display.MonitorName, [], OutputDirectory: _outputDirectory);

        Assert.True(sut.Start(request).Success);
        var second = sut.Start(request);
        await sut.StopAsync();

        Assert.False(second.Success);
        Assert.Equal("recording_already_active", second.ErrorCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task StartAndStopAsync_WithFfmpeg_ProducesMp4AndSidecars()
    {
        var display = new DisplayCaptureEngine().GetDisplays().First();
        using var sut = new DirectRecordingService(
            new DisplayCaptureEngine(),
            new FfmpegDirectVideoWriterFactory());

        var start = sut.Start(new DirectRecordingRequest(
            display.MonitorName,
            [],
            FramesPerSecond: 1,
            OutputDirectory: _outputDirectory));
        Assert.True(start.Success, start.ErrorMessage);
        await Task.Delay(TimeSpan.FromMilliseconds(1250));
        var stop = await sut.StopAsync();

        Assert.True(stop.Success, stop.ErrorMessage);
        var artifact = Assert.IsType<DirectRecordingArtifact>(stop.Artifact);
        Assert.True(new FileInfo(artifact.Path).Length > 0);
        Assert.True(File.Exists($"{artifact.Path}.metadata.json"));
        Assert.True(File.Exists(artifact.EventSidecarPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDirectory))
        {
            Directory.Delete(_outputDirectory, recursive: true);
        }
    }

    private static byte[] GetPixel(byte[] frame, int width, int x, int y)
    {
        var offset = ((y * width) + x) * 4;
        return frame[offset..(offset + 4)];
    }

    private sealed class FakeDisplayCaptureEngine(DisplayDescriptor display) : IDisplayCaptureEngine
    {
        public IReadOnlyList<DisplayDescriptor> GetDisplays()
        {
            return [display];
        }

        public Bitmap Capture(PixelBounds boundsPixels)
        {
            var bitmap = new Bitmap(boundsPixels.Width, boundsPixels.Height);
            for (var y = 0; y < boundsPixels.Height; y++)
            {
                for (var x = 0; x < boundsPixels.Width; x++)
                {
                    bitmap.SetPixel(x, y, Color.FromArgb(255, x, y, 255 - x));
                }
            }

            return bitmap;
        }

        public CapturedMonitor CaptureMonitor(string monitorName)
        {
            return new CapturedMonitor(display, Capture(display.BoundsPixels));
        }
    }

    private sealed class FakeVideoWriterFactory : IDirectVideoWriterFactory
    {
        public FakeVideoWriter Writer { get; } = new();

        public IDirectVideoWriter Create(int width, int height, int framesPerSecond, string outputPath)
        {
            Writer.OutputPath = outputPath;
            return Writer;
        }
    }

    private sealed class FakeVideoWriter : IDirectVideoWriter
    {
        public List<byte[]> Frames { get; } = [];
        public string? OutputPath { get; set; }

        public void WriteFrame(byte[] frameData)
        {
            if (Frames.Count == 0)
            {
                Frames.Add((byte[])frameData.Clone());
            }
        }

        public void Dispose()
        {
            if (!string.IsNullOrWhiteSpace(OutputPath) && !File.Exists(OutputPath))
            {
                File.WriteAllBytes(OutputPath, [1, 2, 3]);
            }
        }
    }
}