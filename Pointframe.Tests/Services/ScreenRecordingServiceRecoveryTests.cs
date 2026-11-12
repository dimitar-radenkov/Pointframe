using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pointframe.Engine;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class ScreenRecordingServiceRecoveryTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeRemuxer _remuxer = new();
    private readonly Mock<ITelemetryService> _telemetry = new();
    private readonly List<IReadOnlyDictionary<string, string>> _failures = [];

    public ScreenRecordingServiceRecoveryTests()
    {
        _telemetry
            .Setup(item => item.TrackEvent(TelemetryEvents.RecordingFailed, It.IsAny<IReadOnlyDictionary<string, string>>()))
            .Callback<string, IReadOnlyDictionary<string, string>?>((_, properties) => _failures.Add(properties!));
    }

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Stop_WhenCleanStop_RemuxesAndReportsComplete()
    {
        var (service, _) = CreateService(new RecoverableWriter(withFragment: true));
        service.Start(0, 0, 100, 100, _folder.File("clean.mp4"));

        service.Stop();

        Assert.Equal(RecordingStopStatus.Complete, service.LastStopResult.Status);
        Assert.Equal(_folder.File("clean.mp4"), service.LastStopResult.ArtifactPath);
        Assert.Equal(TimeSpan.FromSeconds(83), service.LastStopResult.Duration);
        Assert.False(service.LastStopFailed);
        Assert.Null(service.LastStopError);
        Assert.True(File.Exists(_folder.File("clean.mp4")));
        Assert.False(File.Exists(RecordingRecovery.GetIntermediatePath(_folder.File("clean.mp4"))));
        Assert.Empty(_failures);
        service.Dispose();
    }

    [Fact]
    public void Stop_WhenFinalizeFailsAfterFragments_ReportsPartialWithRecoveredTelemetry()
    {
        var writer = new RecoverableWriter(withFragment: true)
        {
            DisposeFailure = new InvalidOperationException("ffmpeg exited with code 1"),
            ExitCode = 1,
            Reason = "ffmpeg_exited",
        };
        var (service, _) = CreateService(writer);
        service.Start(0, 0, 100, 100, _folder.File("finalize.mp4"));

        service.Stop();

        Assert.Equal(RecordingStopStatus.Partial, service.LastStopResult.Status);
        Assert.Equal(_folder.File("finalize.mp4"), service.LastStopResult.ArtifactPath);
        Assert.Equal(TimeSpan.FromSeconds(83), service.LastStopResult.Duration);
        Assert.False(service.LastStopFailed);
        Assert.Null(service.LastStopError);
        var failure = Assert.Single(_failures);
        Assert.Equal("finalize", failure[TelemetryPropertyKeys.Phase]);
        Assert.Equal("ffmpeg_exited", failure[TelemetryPropertyKeys.Reason]);
        Assert.Equal("1", failure[TelemetryPropertyKeys.FfmpegExitCode]);
        Assert.Equal("true", failure[TelemetryPropertyKeys.Recovered]);
        Assert.Equal("1-5m", failure[TelemetryPropertyKeys.RecoveredDurationBucket]);
        Assert.True(TelemetryEventCatalog.Validate(TelemetryEvents.RecordingFailed, failure.ToDictionary(pair => pair.Key, pair => pair.Value)).IsValid);
        service.Dispose();
    }

    [Fact]
    public void Stop_WhenPipeBreaksMidRecording_RecoversAndKeepsTheEncodePhase()
    {
        var writer = new RecoverableWriter(withFragment: true)
        {
            WriteFailure = new IOException("pipe broken"),
            DisposeFailure = new InvalidOperationException("ffmpeg exited with code 1"),
            ExitCode = 1,
            Reason = "ffmpeg_exited",
        };
        var (service, _) = CreateService(writer);
        service.Start(0, 0, 100, 100, _folder.File("pipe.mp4"));
        Assert.True(writer.WriteStarted.Wait(TimeSpan.FromSeconds(10)), "The frame write did not start.");

        service.Stop();

        Assert.Equal(RecordingStopStatus.Partial, service.LastStopResult.Status);
        var failure = Assert.Single(_failures);
        Assert.Equal("encode", failure[TelemetryPropertyKeys.Phase]);
        Assert.Equal("pipe_broken", failure[TelemetryPropertyKeys.Reason]);
        Assert.Equal("true", failure[TelemetryPropertyKeys.Recovered]);
        service.Dispose();
    }

    [Fact]
    public void Stop_WhenNothingWasEncoded_ReportsFailedAndLeavesNoFiles()
    {
        var writer = new RecoverableWriter(withFragment: false)
        {
            DisposeFailure = new InvalidOperationException("ffmpeg exited with code 1"),
            ExitCode = 1,
            Reason = "ffmpeg_exited",
        };
        var (service, _) = CreateService(writer);
        service.Start(0, 0, 100, 100, _folder.File("none.mp4"));

        service.Stop();

        Assert.Equal(RecordingStopStatus.Failed, service.LastStopResult.Status);
        Assert.Null(service.LastStopResult.ArtifactPath);
        Assert.True(service.LastStopFailed);
        Assert.Equal("Recording failed and was not saved. Check that ffmpeg is installed and working.", service.LastStopError);
        Assert.Empty(Directory.GetFiles(_folder.Path, "*.mp4"));
        var failure = Assert.Single(_failures);
        Assert.Equal("false", failure[TelemetryPropertyKeys.Recovered]);
        Assert.DoesNotContain(TelemetryPropertyKeys.RecoveredDurationBucket, failure.Keys);
        service.Dispose();
    }

    [Fact]
    public void Stop_WhenCleanStopHasNoFragment_ReportsFailedWithFinalizePhase()
    {
        var (service, _) = CreateService(new RecoverableWriter(withFragment: false));
        service.Start(0, 0, 100, 100, _folder.File("empty.mp4"));

        service.Stop();

        Assert.Equal(RecordingStopStatus.Failed, service.LastStopResult.Status);
        Assert.True(service.LastStopFailed);
        Assert.Equal("Recording failed and was not saved.", service.LastStopError);
        var failure = Assert.Single(_failures);
        Assert.Equal("finalize", failure[TelemetryPropertyKeys.Phase]);
        Assert.Equal("no_data", failure[TelemetryPropertyKeys.Reason]);
        service.Dispose();
    }

    [Fact]
    public void Stop_WhenRemuxFails_KeepsVisibleRecoveredFileAndReportsPartial()
    {
        _remuxer.RemuxResult = null;
        _remuxer.ReadResult = TimeSpan.FromSeconds(12);
        var (service, _) = CreateService(new RecoverableWriter(withFragment: true));
        service.Start(0, 0, 100, 100, _folder.File("remux.mp4"));

        service.Stop();

        var recoveredPath = _folder.File("remux.recovered.mp4");
        Assert.Equal(RecordingStopStatus.Partial, service.LastStopResult.Status);
        Assert.Equal(recoveredPath, service.LastStopResult.ArtifactPath);
        Assert.Equal(TimeSpan.FromSeconds(12), service.LastStopResult.Duration);
        Assert.False(service.LastStopFailed);
        Assert.True(File.Exists(recoveredPath));
        Assert.False(File.Exists(_folder.File("remux.mp4")));
        Assert.False(File.Exists(RecordingRecovery.GetIntermediatePath(_folder.File("remux.mp4"))));
        var failure = Assert.Single(_failures);
        Assert.Equal("finalize", failure[TelemetryPropertyKeys.Phase]);
        Assert.Equal("remux_failed", failure[TelemetryPropertyKeys.Reason]);
        Assert.Equal("true", failure[TelemetryPropertyKeys.Recovered]);
        Assert.Equal("10-60s", failure[TelemetryPropertyKeys.RecoveredDurationBucket]);
        service.Dispose();
    }

    [Theory]
    [InlineData(5, "<10s")]
    [InlineData(10, "10-60s")]
    [InlineData(59, "10-60s")]
    [InlineData(60, "1-5m")]
    [InlineData(299, "1-5m")]
    [InlineData(300, "5-30m")]
    [InlineData(1799, "5-30m")]
    [InlineData(1800, ">30m")]
    public void Stop_BucketsTheRecoveredDuration(int seconds, string expectedBucket)
    {
        _remuxer.RemuxResult = TimeSpan.FromSeconds(seconds);
        var writer = new RecoverableWriter(withFragment: true) { DisposeFailure = new IOException("finalize failed") };
        var (service, _) = CreateService(writer);
        service.Start(0, 0, 100, 100, _folder.File("bucket.mp4"));

        service.Stop();

        Assert.Equal(expectedBucket, Assert.Single(_failures)[TelemetryPropertyKeys.RecoveredDurationBucket]);
        service.Dispose();
    }

    [Fact]
    public void Stop_WhenCalledAgainAfterFailure_DoesNotThrowOrReportTwice()
    {
        var writer = new RecoverableWriter(withFragment: true) { DisposeFailure = new IOException("finalize failed") };
        var (service, _) = CreateService(writer);
        service.Start(0, 0, 100, 100, _folder.File("again.mp4"));
        service.Stop();
        var first = service.LastStopResult;

        var exception = Record.Exception(() =>
        {
            service.Stop();
            service.Stop();
            service.Dispose();
        });

        Assert.Null(exception);
        Assert.Single(_failures);
        Assert.Equal(first, service.LastStopResult);
        Assert.Equal(1, writer.DisposeCount);
    }

    [Fact]
    public void Start_AfterAFailedStop_ResetsTheStopResult()
    {
        var writer = new RecoverableWriter(withFragment: false) { DisposeFailure = new IOException("finalize failed") };
        var (service, factory) = CreateService(writer);
        service.Start(0, 0, 100, 100, _folder.File("first.mp4"));
        service.Stop();
        Assert.Equal(RecordingStopStatus.Failed, service.LastStopResult.Status);
        factory.Setup(item => item.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns<int, int, int, string, string?>((_, _, _, output, _) => new RecoverableWriter(output, withFragment: true));

        service.Start(0, 0, 100, 100, _folder.File("second.mp4"));
        service.Stop();

        Assert.Equal(RecordingStopStatus.Complete, service.LastStopResult.Status);
        Assert.Equal(_folder.File("second.mp4"), service.LastStopResult.ArtifactPath);
        service.Dispose();
    }

    private (ScreenRecordingService Service, Mock<IVideoWriterFactory> Factory) CreateService(RecoverableWriter writer)
    {
        var factory = new Mock<IVideoWriterFactory>();
        factory
            .Setup(item => item.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns<int, int, int, string, string?>((_, _, _, output, _) =>
            {
                writer.Attach(output);
                return writer;
            });
        var service = new ScreenRecordingService(
            NullLogger<ScreenRecordingService>.Instance,
            Mock.Of<IMicrophoneDeviceService>(),
            Mock.Of<IUserSettingsService>(settings => settings.Current == new UserSettings()),
            factory.Object,
            new IdleFrameCapture(),
            _telemetry.Object,
            new RecordingRecovery(_remuxer, NullLogger<RecordingRecovery>.Instance));
        return (service, factory);
    }

    private sealed class IdleFrameCapture : IRawFrameCapture
    {
        public void Capture(byte[] frameData) => Array.Clear(frameData);

        public void Dispose()
        {
        }
    }

    private sealed class RecoverableWriter : IVideoWriter, IRecoverableVideoWriter, IRecordingFailureDiagnostics
    {
        private readonly bool _withFragment;

        public RecoverableWriter(bool withFragment)
        {
            _withFragment = withFragment;
        }

        public RecoverableWriter(string outputPath, bool withFragment)
            : this(withFragment)
        {
            Attach(outputPath);
        }

        public ManualResetEventSlim WriteStarted { get; } = new();
        public Exception? WriteFailure { get; init; }
        public Exception? DisposeFailure { get; init; }
        public int? ExitCode { get; init; }
        public string? Reason { get; init; }
        public int DisposeCount { get; private set; }
        public string IntermediatePath { get; private set; } = string.Empty;
        public int? FfmpegExitCode => DisposeCount > 0 ? ExitCode : null;
        public string RecentStandardError => string.Empty;
        public string? FailureReason => DisposeCount > 0 ? Reason : null;

        public void Attach(string outputPath)
        {
            IntermediatePath = RecordingRecovery.GetIntermediatePath(outputPath);
            FragmentedMp4Fixture.Write(IntermediatePath, _withFragment);
        }

        public void WriteFrame(byte[] frameData)
        {
            WriteStarted.Set();
            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }
        }

        public void Dispose()
        {
            DisposeCount++;
            if (DisposeFailure is not null)
            {
                throw DisposeFailure;
            }
        }
    }
}
