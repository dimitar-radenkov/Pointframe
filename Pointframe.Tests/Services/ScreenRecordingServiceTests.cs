using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pointframe.Engine;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class ScreenRecordingServiceTests
{
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return NoopDisposable.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static NoopDisposable Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class TestVideoWriter : IVideoWriter
    {
        private readonly TimeSpan _writeDelay;

        public TestVideoWriter(TimeSpan writeDelay)
        {
            _writeDelay = writeDelay;
        }

        public int WrittenFrameCount { get; private set; }

        public void WriteFrame(byte[] frameData)
        {
            if (_writeDelay > TimeSpan.Zero)
            {
                Thread.Sleep(_writeDelay);
            }

            WrittenFrameCount++;
        }

        public void Dispose()
        {
        }
    }

    private sealed class BlockingVideoWriter : IVideoWriter
    {
        private readonly ManualResetEventSlim _releaseWrites = new();

        public ManualResetEventSlim FirstWriteStarted { get; } = new();

        public void WriteFrame(byte[] frameData)
        {
            FirstWriteStarted.Set();
            _releaseWrites.Wait();
        }

        public void ReleaseWrites()
        {
            _releaseWrites.Set();
        }

        public void Dispose()
        {
            _releaseWrites.Set();
            _releaseWrites.Dispose();
            FirstWriteStarted.Dispose();
        }
    }

    private sealed class SignalingFrameCapture : IRawFrameCapture
    {
        private int _captureCount;

        public ManualResetEventSlim SecondFrameCaptured { get; } = new();

        public void Capture(byte[] frameData)
        {
            Array.Clear(frameData);
            if (Interlocked.Increment(ref _captureCount) == 2)
            {
                SecondFrameCaptured.Set();
            }
        }

        public void Dispose()
        {
            SecondFrameCaptured.Dispose();
        }
    }

    private sealed class FrameCollectingVideoWriter : IVideoWriter
    {
        public List<byte[]> Frames { get; } = [];

        public void WriteFrame(byte[] frameData)
        {
            Frames.Add(frameData);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FailingFrameCapture : IRawFrameCapture
    {
        public ManualResetEventSlim Failed { get; } = new();

        public void Capture(byte[] frameData)
        {
            Failed.Set();
            throw new InvalidOperationException("capture failed");
        }

        public void Dispose() => Failed.Dispose();
    }

    private static ScreenRecordingService CreateSut() =>
        new(NullLogger<ScreenRecordingService>.Instance,
            Mock.Of<IMicrophoneDeviceService>(),
            Mock.Of<IUserSettingsService>(s => s.Current == new UserSettings()),
            new Mock<IVideoWriterFactory>().Object);

    private static ScreenRecordingService CreateSut(
        IVideoWriterFactory factory,
        IMicrophoneDeviceService? microphoneDeviceService = null,
        UserSettings? settings = null,
        ILogger<ScreenRecordingService>? logger = null,
        IRawFrameCapture? frameCapture = null) =>
        new(logger ?? NullLogger<ScreenRecordingService>.Instance,
            microphoneDeviceService ?? Mock.Of<IMicrophoneDeviceService>(),
            Mock.Of<IUserSettingsService>(s => s.Current == (settings ?? new UserSettings())),
            factory,
            frameCapture);

    [Fact]
    public void IsRecording_IsFalse_BeforeStart()
    {
        // Arrange
        using var svc = CreateSut();

        // Assert
        Assert.False(svc.IsRecording);
    }

    [Fact]
    public void Stop_WhenNotRecording_DoesNotThrow()
    {
        // Arrange
        using var svc = CreateSut();

        // Act
        var ex = Record.Exception(() => svc.Stop());

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_WhenNotRecording_DoesNotThrow()
    {
        // Arrange
        var svc = CreateSut();

        // Act
        var ex = Record.Exception(() => svc.Dispose());

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public void Start_WithOddDimensions_TruncatesToEven()
    {
        // Arrange
        using var svc = CreateSut();

        // Act
        svc.Start(0, 0, 1, 1, System.IO.Path.GetTempFileName());

        // Assert
        Assert.False(svc.IsRecording);
    }

    [Fact]
    public void Start_CallsFactoryWithRecordingDimensionsAndOutputPath()
    {
        // Arrange
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new Mock<IVideoWriter>().Object);
        using var svc = CreateSut(mockFactory.Object);

        // Act
        svc.Start(0, 0, 100, 100, System.IO.Path.GetTempFileName());

        // Assert
        mockFactory.Verify(f => f.Create(100, 100, It.IsAny<int>(), It.IsAny<string>(), null), Times.Once);
    }

    [Fact]
    public void Start_PassesCorrectDimensionsAndFpsToFactory()
    {
        // Arrange
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new Mock<IVideoWriter>().Object);
        var settings = new UserSettings { RecordingFps = 30 };
        using var svc = CreateSut(mockFactory.Object, settings: settings);

        // Act
        svc.Start(10, 20, 200, 150, "test.mp4");

        // Assert
        mockFactory.Verify(f => f.Create(200, 150, 30, "test.mp4", null), Times.Once);
    }

    [Fact]
    public void Start_FactoryThrowsFileNotFound_PropagatesException()
    {
        // Arrange
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Throws(new System.IO.FileNotFoundException("ffmpeg.exe not found"));
        using var svc = CreateSut(mockFactory.Object);

        // Act & Assert
        Assert.Throws<System.IO.FileNotFoundException>(() =>
            svc.Start(0, 0, 100, 100, "test.mp4"));
    }

    [Fact]
    public void Start_FactoryThrowsFileNotFound_IsRecordingRemainsFalse()
    {
        // Arrange
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Throws(new System.IO.FileNotFoundException("ffmpeg.exe not found"));
        using var svc = CreateSut(mockFactory.Object);

        // Act
        try
        {
            svc.Start(0, 0, 100, 100, "test.mp4");
        }
        catch { }

        // Assert
        Assert.False(svc.IsRecording);
    }

    [Fact]
    public void Start_WhenInitializationFailsAfterWriterCreation_ResetsRecordingStateAndClearsBufferPool()
    {
        var writerMock = new Mock<IVideoWriter>();
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(writerMock.Object);
        var microphoneService = new Mock<IMicrophoneDeviceService>();
        microphoneService.Setup(service => service.GetAvailableCaptureDeviceNames()).Returns(["Studio Mic"]);
        microphoneService.Setup(service => service.GetDefaultCaptureDeviceName()).Returns("Studio Mic");
        microphoneService.Setup(service => service.TryGetCaptureDeviceMuted("Studio Mic")).Throws(new InvalidOperationException("boom"));
        var settings = new UserSettings { RecordMicrophone = true };
        using var svc = CreateSut(mockFactory.Object, microphoneService.Object, settings);

        Assert.Throws<InvalidOperationException>(() => svc.Start(0, 0, 100, 100, "test.mp4"));

        Assert.False(svc.IsRecording);
        Assert.False(svc.IsPaused);
        Assert.False(svc.IsRecordingMicrophoneEnabled);
        Assert.False(svc.CanToggleMicrophone);
        Assert.False(svc.IsMicrophoneMuted);
    }

    [Fact]
    public void Start_EvenDimensions_SetsIsRecordingTrue()
    {
        // Arrange
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new Mock<IVideoWriter>().Object);
        using var svc = CreateSut(mockFactory.Object);

        // Act
        svc.Start(0, 0, 100, 100, "test.mp4");

        // Assert
        Assert.True(svc.IsRecording);
    }

    [Fact]
    public void Stop_WhenRecording_DisposesWriter()
    {
        // Arrange
        var writerMock = new Mock<IVideoWriter>();
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(writerMock.Object);
        using var svc = CreateSut(mockFactory.Object);
        svc.Start(0, 0, 100, 100, "test.mp4");

        // Act
        svc.Stop();

        // Assert
        Assert.False(svc.IsRecording);
        writerMock.Verify(w => w.Dispose(), Times.Once);
    }

    [Fact]
    public void Stop_WhenCaptureWorkerFails_ContainsFailureAndEmitsRecordingFailedOnce()
    {
        var writer = new Mock<IVideoWriter>();
        var factory = new Mock<IVideoWriterFactory>();
        factory.Setup(item => item.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>())).Returns(writer.Object);
        var microphone = new Mock<IMicrophoneDeviceService>();
        microphone.Setup(item => item.GetAvailableCaptureDeviceNames()).Returns(["Studio Mic"]);
        microphone.Setup(item => item.GetDefaultCaptureDeviceName()).Returns("Studio Mic");
        microphone.Setup(item => item.TryGetCaptureDeviceMuted("Studio Mic")).Returns(true);
        var telemetry = new Mock<ITelemetryService>();
        using var capture = new FailingFrameCapture();
        IReadOnlyDictionary<string, string>? failureProperties = null;
        telemetry.Setup(item => item.TrackEvent(TelemetryEvents.RecordingFailed, It.IsAny<IReadOnlyDictionary<string, string>>()))
            .Callback<string, IReadOnlyDictionary<string, string>?>((_, properties) => failureProperties = properties);
        using var service = new ScreenRecordingService(
            NullLogger<ScreenRecordingService>.Instance,
            microphone.Object,
            Mock.Of<IUserSettingsService>(settings => settings.Current == new UserSettings { RecordMicrophone = true }),
            factory.Object,
            capture,
            telemetry.Object);
        service.Start(0, 0, 100, 100, "recording.mp4");
        Assert.True(capture.Failed.Wait(TimeSpan.FromSeconds(10)), "The capture worker did not fail.");

        var exception = Record.Exception(service.Stop);
        service.Stop();

        Assert.Null(exception);
        Assert.True(service.LastStopFailed);
        Assert.False(service.IsRecording);
        writer.Verify(item => item.Dispose(), Times.Once);
        microphone.Verify(item => item.TrySetCaptureDeviceMuted("Studio Mic", true), Times.Once);
        telemetry.Verify(item => item.TrackEvent(TelemetryEvents.RecordingFailed, It.IsAny<IReadOnlyDictionary<string, string>>()), Times.Once);
        Assert.NotNull(failureProperties);
        Assert.Equal("capture", failureProperties[TelemetryPropertyKeys.Phase]);
        Assert.Equal("capture_failed", failureProperties[TelemetryPropertyKeys.Reason]);
        Assert.Contains(nameof(InvalidOperationException), failureProperties[TelemetryPropertyKeys.InnerTypes]);
    }

    [Fact]
    public void Stop_WhenWriterPipeFails_ReportsEncodeFailureAndDisposesWriter()
    {
        using var capture = new SignalingFrameCapture();
        var writeStarted = new ManualResetEventSlim();
        var writer = new Mock<IVideoWriter>();
        writer.Setup(item => item.WriteFrame(It.IsAny<byte[]>())).Callback(() =>
        {
            writeStarted.Set();
            throw new IOException("pipe broken");
        });
        var factory = new Mock<IVideoWriterFactory>();
        factory.Setup(item => item.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>())).Returns(writer.Object);
        var telemetry = new Mock<ITelemetryService>();
        IReadOnlyDictionary<string, string>? failureProperties = null;
        telemetry.Setup(item => item.TrackEvent(TelemetryEvents.RecordingFailed, It.IsAny<IReadOnlyDictionary<string, string>>()))
            .Callback<string, IReadOnlyDictionary<string, string>?>((_, properties) => failureProperties = properties);
        using var service = new ScreenRecordingService(
            NullLogger<ScreenRecordingService>.Instance,
            Mock.Of<IMicrophoneDeviceService>(),
            Mock.Of<IUserSettingsService>(settings => settings.Current == new UserSettings()),
            factory.Object,
            capture,
            telemetry.Object);
        service.Start(0, 0, 100, 100, "recording.mp4");

        Assert.True(writeStarted.Wait(TimeSpan.FromSeconds(10)), "The frame write did not start.");
        service.Stop();

        Assert.True(service.LastStopFailed);
        Assert.Equal("encode", failureProperties![TelemetryPropertyKeys.Phase]);
        Assert.Equal("pipe_broken", failureProperties[TelemetryPropertyKeys.Reason]);
        writer.Verify(item => item.Dispose(), Times.Once);
    }

    [Fact]
    public void Stop_WhenWriterFinalizeFails_ReportsFinalizeFailure()
    {
        var writer = new Mock<IVideoWriter>();
        writer.Setup(item => item.Dispose()).Throws(new IOException("finalize failed"));
        var factory = new Mock<IVideoWriterFactory>();
        factory.Setup(item => item.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>())).Returns(writer.Object);
        var telemetry = new Mock<ITelemetryService>();
        IReadOnlyDictionary<string, string>? failureProperties = null;
        telemetry.Setup(item => item.TrackEvent(TelemetryEvents.RecordingFailed, It.IsAny<IReadOnlyDictionary<string, string>>()))
            .Callback<string, IReadOnlyDictionary<string, string>?>((_, properties) => failureProperties = properties);
        using var service = new ScreenRecordingService(
            NullLogger<ScreenRecordingService>.Instance,
            Mock.Of<IMicrophoneDeviceService>(),
            Mock.Of<IUserSettingsService>(settings => settings.Current == new UserSettings()),
            factory.Object,
            new SignalingFrameCapture(),
            telemetry.Object);
        service.Start(0, 0, 100, 100, "recording.mp4");

        service.Stop();

        Assert.True(service.LastStopFailed);
        Assert.Equal("finalize", failureProperties![TelemetryPropertyKeys.Phase]);
        Assert.Equal("unknown", failureProperties[TelemetryPropertyKeys.Reason]);
        writer.Verify(item => item.Dispose(), Times.Once);
    }

    [Fact]
    public void Start_TwiceConcurrently_IgnoresSecondCall()
    {
        // Arrange
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new Mock<IVideoWriter>().Object);
        using var svc = CreateSut(mockFactory.Object);
        svc.Start(0, 0, 100, 100, "first.mp4");

        // Act
        svc.Start(0, 0, 200, 200, "second.mp4");

        // Assert — factory only called once
        mockFactory.Verify(
            f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public void Start_OddDimensions_TruncatesToEvenBeforeFactory()
    {
        // Arrange
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new Mock<IVideoWriter>().Object);
        using var svc = CreateSut(mockFactory.Object);

        // Act
        svc.Start(0, 0, 101, 151, "test.mp4");

        // Assert — dimensions truncated to 100×150
        mockFactory.Verify(f => f.Create(100, 150, It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public void Pause_WhenNotRecording_DoesNothing()
    {
        using var svc = CreateSut();

        svc.Pause();

        Assert.False(svc.IsPaused);
    }

    [Fact]
    public void Resume_WhenNotRecording_DoesNothing()
    {
        using var svc = CreateSut();

        svc.Resume();

        Assert.False(svc.IsPaused);
    }

    [Fact]
    public void Pause_WhenRecording_SetsIsPausedTrue()
    {
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new Mock<IVideoWriter>().Object);
        using var svc = CreateSut(mockFactory.Object);

        svc.Start(0, 0, 100, 100, "test.mp4");
        svc.Pause();

        Assert.True(svc.IsPaused);
    }

    [Fact]
    public void Resume_WhenPaused_ClearsIsPaused()
    {
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new Mock<IVideoWriter>().Object);
        using var svc = CreateSut(mockFactory.Object);

        svc.Start(0, 0, 100, 100, "test.mp4");
        svc.Pause();
        svc.Resume();

        Assert.False(svc.IsPaused);
    }

    [Fact]
    public void Stop_AfterPauseAndResume_FinalizesLifecycleEventTrack()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pointframe-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var outputPath = Path.Combine(directory, "recording.mp4");
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(factory => factory.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new Mock<IVideoWriter>().Object);

        try
        {
            using var svc = CreateSut(mockFactory.Object);
            svc.Start(0, 0, 100, 100, outputPath);
            svc.Pause();
            svc.Resume();
            Assert.True(svc.TryAddRedaction(new Int32Rect(10, 20, 30, 40)));
            svc.Stop();

            var summary = Assert.IsType<RecordingEventTrackSummary>(svc.EventTrackSummary);
            var events = File.ReadLines(summary.SidecarPath)
                .Select(line => JsonSerializer.Deserialize<RecordingEvent>(line))
                .ToArray();

            Assert.Equal(5, summary.EventCount);
            Assert.Collection(events,
                recordingEvent => Assert.Equal("recording.started", recordingEvent?.EventType),
                recordingEvent => Assert.Equal("recording.paused", recordingEvent?.EventType),
                recordingEvent => Assert.Equal("recording.resumed", recordingEvent?.EventType),
                recordingEvent =>
                {
                    Assert.Equal("redaction.added", recordingEvent?.EventType);
                    Assert.Equal(10, recordingEvent?.Payload.RedactionX);
                    Assert.Equal(20, recordingEvent?.Payload.RedactionY);
                    Assert.Equal(30, recordingEvent?.Payload.RedactionWidth);
                    Assert.Equal(40, recordingEvent?.Payload.RedactionHeight);
                },
                recordingEvent => Assert.Equal("recording.stopped", recordingEvent?.EventType));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Start_RecordMicrophoneEnabled_UsesDefaultCaptureDeviceName()
    {
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new Mock<IVideoWriter>().Object);
        var microphoneService = Mock.Of<IMicrophoneDeviceService>(service =>
            service.GetAvailableCaptureDeviceNames() == new[] { "Studio Mic", "USB Mic" } &&
            service.GetDefaultCaptureDeviceName() == "Studio Mic" &&
            service.TryGetCaptureDeviceMuted("Studio Mic") == false);
        var settings = new UserSettings { RecordMicrophone = true };

        using var svc = CreateSut(mockFactory.Object, microphoneService, settings);

        svc.Start(0, 0, 100, 100, "test.mp4");

        mockFactory.Verify(f => f.Create(100, 100, It.IsAny<int>(), "test.mp4", "Studio Mic"), Times.Once);
        Assert.True(svc.IsRecordingMicrophoneEnabled);
        Assert.True(svc.CanToggleMicrophone);
        Assert.False(svc.IsMicrophoneMuted);
    }

    [Fact]
    public void Start_RecordMicrophoneEnabled_UsesConfiguredCaptureDeviceWhenAvailable()
    {
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new Mock<IVideoWriter>().Object);
        var microphoneService = Mock.Of<IMicrophoneDeviceService>(service =>
            service.GetAvailableCaptureDeviceNames() == new[] { "Studio Mic", "USB Mic" } &&
            service.GetDefaultCaptureDeviceName() == "Studio Mic" &&
            service.TryGetCaptureDeviceMuted("USB Mic") == false);
        var settings = new UserSettings
        {
            RecordMicrophone = true,
            RecordingMicrophoneDeviceName = "USB Mic",
        };

        using var svc = CreateSut(mockFactory.Object, microphoneService, settings);

        svc.Start(0, 0, 100, 100, "test.mp4");

        mockFactory.Verify(f => f.Create(100, 100, It.IsAny<int>(), "test.mp4", "USB Mic"), Times.Once);
        Assert.True(svc.IsRecordingMicrophoneEnabled);
    }

    [Fact]
    public void Start_RecordMicrophoneEnabled_FallsBackToDefaultWhenConfiguredDeviceUnavailable()
    {
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new Mock<IVideoWriter>().Object);
        var microphoneService = Mock.Of<IMicrophoneDeviceService>(service =>
            service.GetAvailableCaptureDeviceNames() == new[] { "Studio Mic", "USB Mic" } &&
            service.GetDefaultCaptureDeviceName() == "Studio Mic" &&
            service.TryGetCaptureDeviceMuted("Studio Mic") == false);
        var settings = new UserSettings
        {
            RecordMicrophone = true,
            RecordingMicrophoneDeviceName = "Missing Mic",
        };

        using var svc = CreateSut(mockFactory.Object, microphoneService, settings);

        svc.Start(0, 0, 100, 100, "test.mp4");

        mockFactory.Verify(f => f.Create(100, 100, It.IsAny<int>(), "test.mp4", "Studio Mic"), Times.Once);
        Assert.True(svc.IsRecordingMicrophoneEnabled);
    }

    [Fact]
    public void Start_RecordMicrophoneEnabledWithoutDevice_FallsBackToVideoOnly()
    {
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new Mock<IVideoWriter>().Object);
        var microphoneService = Mock.Of<IMicrophoneDeviceService>(service =>
            service.GetAvailableCaptureDeviceNames() == Array.Empty<string>() &&
            service.GetDefaultCaptureDeviceName() == null);
        var settings = new UserSettings { RecordMicrophone = true };

        using var svc = CreateSut(mockFactory.Object, microphoneService, settings);

        svc.Start(0, 0, 100, 100, "test.mp4");

        mockFactory.Verify(f => f.Create(100, 100, It.IsAny<int>(), "test.mp4", null), Times.Once);
        Assert.False(svc.IsRecordingMicrophoneEnabled);
        Assert.False(svc.CanToggleMicrophone);
    }

    [Fact]
    public void Stop_WhenMicrophoneRecording_ResetsMicrophoneEnabledState()
    {
        var writerMock = new Mock<IVideoWriter>();
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(writerMock.Object);
        var microphoneService = Mock.Of<IMicrophoneDeviceService>(service =>
            service.GetAvailableCaptureDeviceNames() == new[] { "Studio Mic" } &&
            service.GetDefaultCaptureDeviceName() == "Studio Mic");
        var settings = new UserSettings { RecordMicrophone = true };

        using var svc = CreateSut(mockFactory.Object, microphoneService, settings);

        svc.Start(0, 0, 100, 100, "test.mp4");
        Assert.True(svc.IsRecordingMicrophoneEnabled);

        svc.Stop();

        Assert.False(svc.IsRecordingMicrophoneEnabled);
    }

    [Fact]
    public void TrySetMicrophoneMuted_WhenControllable_UpdatesMuteState()
    {
        var writerMock = new Mock<IVideoWriter>();
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(writerMock.Object);
        var microphoneService = new Mock<IMicrophoneDeviceService>();
        microphoneService.Setup(service => service.GetAvailableCaptureDeviceNames()).Returns(["Studio Mic"]);
        microphoneService.Setup(service => service.GetDefaultCaptureDeviceName()).Returns("Studio Mic");
        microphoneService.Setup(service => service.TryGetCaptureDeviceMuted("Studio Mic")).Returns(false);
        microphoneService.Setup(service => service.TrySetCaptureDeviceMuted("Studio Mic", true)).Returns(true);
        var settings = new UserSettings { RecordMicrophone = true };

        using var svc = CreateSut(mockFactory.Object, microphoneService.Object, settings);

        svc.Start(0, 0, 100, 100, "test.mp4");
        var result = svc.TrySetMicrophoneMuted(true);

        Assert.True(result);
        Assert.True(svc.IsMicrophoneMuted);
        microphoneService.Verify(service => service.TrySetCaptureDeviceMuted("Studio Mic", true), Times.Once);
    }

    [Fact]
    public void Stop_WhenMicrophoneMuteChanged_RestoresInitialMuteState()
    {
        var writerMock = new Mock<IVideoWriter>();
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(writerMock.Object);
        var microphoneService = new Mock<IMicrophoneDeviceService>();
        microphoneService.Setup(service => service.GetAvailableCaptureDeviceNames()).Returns(["Studio Mic"]);
        microphoneService.Setup(service => service.GetDefaultCaptureDeviceName()).Returns("Studio Mic");
        microphoneService.Setup(service => service.TryGetCaptureDeviceMuted("Studio Mic")).Returns(false);
        microphoneService.Setup(service => service.TrySetCaptureDeviceMuted("Studio Mic", true)).Returns(true);
        microphoneService.Setup(service => service.TrySetCaptureDeviceMuted("Studio Mic", false)).Returns(true);
        var settings = new UserSettings { RecordMicrophone = true };

        using var svc = CreateSut(mockFactory.Object, microphoneService.Object, settings);

        svc.Start(0, 0, 100, 100, "test.mp4");
        svc.TrySetMicrophoneMuted(true);
        svc.Stop();

        microphoneService.Verify(service => service.TrySetCaptureDeviceMuted("Studio Mic", true), Times.Once);
        microphoneService.Verify(service => service.TrySetCaptureDeviceMuted("Studio Mic", false), Times.Once);
    }

    [Fact]
    public void Stop_WhenWriterBackpressureOccurs_PadsFramesToElapsedDuration()
    {
        var writer = new TestVideoWriter(TimeSpan.FromMilliseconds(180));
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(writer);
        var settings = new UserSettings { RecordingFps = 10 };

        using var svc = CreateSut(mockFactory.Object, settings: settings);

        var stopwatch = Stopwatch.StartNew();
        svc.Start(0, 0, 100, 100, "test.mp4");
        Thread.Sleep(650);
        var elapsedBeforeStop = stopwatch.Elapsed;
        svc.Stop();
        stopwatch.Stop();

        var minimumExpectedFrames = (int)Math.Floor(elapsedBeforeStop.TotalSeconds * settings.RecordingFps) - 1;

        Assert.True(writer.WrittenFrameCount >= minimumExpectedFrames,
            $"Expected at least {minimumExpectedFrames} written frames for elapsed time {elapsedBeforeStop}, but saw {writer.WrittenFrameCount}.");
    }

    [Fact]
    public void Stop_WhenWriterBackpressureOccursDuringMicrophoneRecording_PadsFramesToElapsedDuration()
    {
        var writer = new TestVideoWriter(TimeSpan.FromMilliseconds(180));
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(writer);
        var microphoneService = Mock.Of<IMicrophoneDeviceService>(service =>
            service.GetAvailableCaptureDeviceNames() == new[] { "Studio Mic" } &&
            service.GetDefaultCaptureDeviceName() == "Studio Mic" &&
            service.TryGetCaptureDeviceMuted("Studio Mic") == false);
        var settings = new UserSettings
        {
            RecordingFps = 10,
            RecordMicrophone = true,
        };

        using var svc = CreateSut(mockFactory.Object, microphoneService, settings);

        var stopwatch = Stopwatch.StartNew();
        svc.Start(0, 0, 100, 100, "test.mp4");
        Thread.Sleep(650);
        var elapsedBeforeStop = stopwatch.Elapsed;
        svc.Stop();
        stopwatch.Stop();

        var minimumExpectedFrames = (int)Math.Floor(elapsedBeforeStop.TotalSeconds * settings.RecordingFps) - 1;

        mockFactory.Verify(f => f.Create(100, 100, settings.RecordingFps, "test.mp4", "Studio Mic"), Times.Once);
        Assert.True(writer.WrittenFrameCount >= minimumExpectedFrames,
            $"Expected at least {minimumExpectedFrames} written frames for elapsed time {elapsedBeforeStop}, but saw {writer.WrittenFrameCount}.");
    }

    [Fact]
    public void Stop_WhenWriterBackpressureOccurs_LogsZeroDroppedFrames()
    {
        var writer = new BlockingVideoWriter();
        var capture = new SignalingFrameCapture();
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(writer);
        var logger = new ListLogger<ScreenRecordingService>();
        var settings = new UserSettings { RecordingFps = 10 };

        using var svc = CreateSut(mockFactory.Object, settings: settings, logger: logger, frameCapture: capture);

        svc.Start(0, 0, 100, 100, "test.mp4");
        Assert.True(writer.FirstWriteStarted.Wait(TimeSpan.FromSeconds(10)), "The first frame write did not start.");
        Assert.True(capture.SecondFrameCaptured.Wait(TimeSpan.FromSeconds(10)), "The second frame was not captured while the writer was blocked.");
        writer.ReleaseWrites();
        svc.Stop();

        var statsMessage = logger.Messages.Last(message => message.StartsWith("Recording session stats:", StringComparison.Ordinal));

        Assert.Contains("droppedFrames=0", statsMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Stop_WhenWriterBackpressureOccurs_LogsZeroDroppedDuration()
    {
        var logger = new ListLogger<ScreenRecordingService>();
        var writer = new BlockingVideoWriter();
        var capture = new SignalingFrameCapture();
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(writer);
        var settings = new UserSettings { RecordingFps = 10 };

        using var svc = CreateSut(mockFactory.Object, settings: settings, logger: logger, frameCapture: capture);

        svc.Start(0, 0, 100, 100, "test.mp4");
        Assert.True(writer.FirstWriteStarted.Wait(TimeSpan.FromSeconds(10)), "The first frame write did not start.");
        Assert.True(capture.SecondFrameCaptured.Wait(TimeSpan.FromSeconds(10)), "The second frame was not captured while the writer was blocked.");
        writer.ReleaseWrites();
        svc.Stop();

        var sessionStats = logger.Messages.Last(message => message.StartsWith("Recording session stats:", StringComparison.Ordinal));

        Assert.Contains("droppedFrames=0", sessionStats, StringComparison.Ordinal);
        Assert.Contains("droppedDuration=00:00:00", sessionStats, StringComparison.Ordinal);
    }

    private static T GetField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field?.GetValue(target));
    }

    private static void SetField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }

    private static void InvokePrivateMethod(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(target, args);
    }
}
