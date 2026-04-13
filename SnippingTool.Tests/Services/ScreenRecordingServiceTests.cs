using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SnippingTool.Models;
using SnippingTool.Services;
using Xunit;

namespace SnippingTool.Tests.Services;

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

    private static ScreenRecordingService CreateSut() =>
        new(NullLogger<ScreenRecordingService>.Instance,
            Mock.Of<IMicrophoneDeviceService>(),
            Mock.Of<IUserSettingsService>(s => s.Current == new UserSettings()),
            new Mock<IVideoWriterFactory>().Object);

    private static ScreenRecordingService CreateSut(
        IVideoWriterFactory factory,
        IMicrophoneDeviceService? microphoneDeviceService = null,
        UserSettings? settings = null,
        ILogger<ScreenRecordingService>? logger = null) =>
        new(logger ?? NullLogger<ScreenRecordingService>.Instance,
            microphoneDeviceService ?? Mock.Of<IMicrophoneDeviceService>(),
            Mock.Of<IUserSettingsService>(s => s.Current == (settings ?? new UserSettings())),
            factory);

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
        var writer = new TestVideoWriter(TimeSpan.FromMilliseconds(180));
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(writer);
        var logger = new ListLogger<ScreenRecordingService>();
        var settings = new UserSettings { RecordingFps = 10 };

        using var svc = CreateSut(mockFactory.Object, settings: settings, logger: logger);

        svc.Start(0, 0, 100, 100, "test.mp4");
        Thread.Sleep(650);
        svc.Stop();

        var statsMessage = logger.Messages.Last(message => message.StartsWith("Recording session stats:", StringComparison.Ordinal));

        Assert.Contains("droppedFrames=0", statsMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Stop_WhenWriterBackpressureOccurs_LogsZeroDroppedDuration()
    {
        var logger = new ListLogger<ScreenRecordingService>();
        var writer = new TestVideoWriter(TimeSpan.FromMilliseconds(180));
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(writer);
        var settings = new UserSettings { RecordingFps = 10 };

        using var svc = CreateSut(mockFactory.Object, settings: settings, logger: logger);

        svc.Start(0, 0, 100, 100, "test.mp4");
        Thread.Sleep(650);
        svc.Stop();

        var sessionStats = logger.Messages.Last(message => message.StartsWith("Recording session stats:", StringComparison.Ordinal));

        Assert.Contains("droppedFrames=0", sessionStats, StringComparison.Ordinal);
        Assert.Contains("droppedDuration=00:00:00", sessionStats, StringComparison.Ordinal);
    }
}
