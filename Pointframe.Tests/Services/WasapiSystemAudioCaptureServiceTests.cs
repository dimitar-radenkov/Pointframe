using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class WasapiSystemAudioCaptureServiceTests : IDisposable
{
    private static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    private readonly TempFolder _folder = new();
    private TimeSpan _now = TimeSpan.Zero;

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Stop_WhenTheCaptureThreadIsInsideACallbackWhileTheSourceIsDisposed_DoesNotDeadlock()
    {
        var source = new FakeSource { DeliverFromDisposeThread = true };
        var service = Create(source);
        Assert.True(service.TryStart(_folder.File("a.wav"), out _));

        var stop = Task.Run(() => service.Stop());

        Assert.True(stop.Wait(TimeSpan.FromSeconds(10)), "Stop deadlocked while joining the capture thread");
        Assert.True(source.Disposed);
    }

    [Fact]
    public void Dispose_WhenTheCaptureThreadIsInsideACallback_DoesNotDeadlock()
    {
        var source = new FakeSource { DeliverFromDisposeThread = true };
        var service = Create(source);
        Assert.True(service.TryStart(_folder.File("b.wav"), out _));

        var dispose = Task.Run(service.Dispose);

        Assert.True(dispose.Wait(TimeSpan.FromSeconds(10)), "Dispose deadlocked while joining the capture thread");
        Assert.False(File.Exists(_folder.File("b.wav")));
    }

    [Fact]
    public void Stop_PadsToTheRecordingLengthAndReturnsTheWav()
    {
        var source = new FakeSource();
        var service = Create(source);
        Assert.True(service.TryStart(_folder.File("c.wav"), out _));
        _now = TimeSpan.FromSeconds(2);

        var result = service.Stop();

        Assert.True(result.HasAudio);
        using var reader = new WaveFileReader(result.WavPath!);
        Assert.InRange(reader.TotalTime.TotalSeconds, 1.99, 2.01);
    }

    [Fact]
    public void Packets_AreWrittenAtTheirOwnTimestamp()
    {
        var source = new FakeSource();
        var service = Create(source);
        Assert.True(service.TryStart(_folder.File("d.wav"), out _));
        _now = TimeSpan.FromSeconds(1);
        var tenMs = new byte[Format.AverageBytesPerSecond / 100];
        for (var i = 0; i < 10; i++)
        {
            source.Raise(tenMs, false, TimeSpan.FromMilliseconds(i * 10));
        }

        var result = service.Stop();

        using var reader = new WaveFileReader(result.WavPath!);
        Assert.InRange(reader.TotalTime.TotalSeconds, 0.99, 1.01);
    }

    [Fact]
    public void LossOfTheDevice_RaisesCaptureFailedAndStopDiscardsTheWav()
    {
        var source = new FakeSource();
        var service = Create(source);
        string? reason = null;
        service.CaptureFailed += value => reason = value;
        Assert.True(service.TryStart(_folder.File("e.wav"), out _));

        source.RaiseStopped(new InvalidOperationException("device removed"));
        var result = service.Stop();

        Assert.Equal("device_lost", reason);
        Assert.False(result.HasAudio);
        Assert.False(File.Exists(_folder.File("e.wav")));
    }

    [Fact]
    public void TryStart_WhenTheSourceCannotStart_FailsAndLeavesNoFile()
    {
        var source = new FakeSource { StartFailure = new InvalidOperationException("no device") };
        var service = Create(source);

        Assert.False(service.TryStart(_folder.File("f.wav"), out var reason));

        Assert.Equal("start_failed", reason);
        Assert.False(File.Exists(_folder.File("f.wav")));
    }

    [Fact]
    public void WriterOptions_EnableRf64SoLongRecordingsPastFourGigabytesSurvive()
    {
        Assert.True(WasapiSystemAudioCaptureService.CreateWriterOptions().EnableRf64);
    }

    private WasapiSystemAudioCaptureService Create(FakeSource source) =>
        new(NullLogger<WasapiSystemAudioCaptureService>.Instance, () => source, () => _now);

    private sealed class FakeSource : ILoopbackSource
    {
        public Exception? StartFailure { get; init; }
        public bool DeliverFromDisposeThread { get; init; }
        public bool Disposed { get; private set; }
        public WaveFormat WaveFormat => Format;
        // Unsubscribing does not cancel a callback that was already dispatched, so removal is ignored here.
        public event LoopbackPacketHandler? PacketAvailable
        {
            add => _packetHandler += value;
            remove { }
        }

        private LoopbackPacketHandler? _packetHandler;
        public event Action<Exception?>? Stopped;

        public void Start()
        {
            if (StartFailure is not null)
            {
                throw StartFailure;
            }
        }

        public void Stop()
        {
        }

        public void Raise(byte[] data, bool silent, TimeSpan time) => _packetHandler?.Invoke(data, silent, time);

        public void RaiseStopped(Exception? exception) => Stopped?.Invoke(exception);

        // Like the real recorder, disposing joins the capture thread, which may be inside a callback.
        public void Dispose()
        {
            Disposed = true;
            if (!DeliverFromDisposeThread)
            {
                return;
            }

            var thread = new Thread(() => Raise(new byte[Format.AverageBytesPerSecond / 100], false, TimeSpan.Zero));
            thread.Start();
            thread.Join();
        }
    }
}
